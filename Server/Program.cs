using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

// ReSharper disable RedundantStringInterpolation

namespace Server
{
    struct Message
    {
        public string Value { get; set; }

        public override string ToString()
        {
            return this.Value;
        }
    }

    class Program
    {
        private Thread _stopper;
        private Thread _errorReporter;
        private Thread _messageReporter;

        private TcpListener _listener;
        private CancellationTokenSource _cts;

        private BlockingCollection<Exception> _errQueue;
        private BlockingCollection<Message> _msgQueue;

        static void Main()
        {
            var me = new Program();

            me.Initialize();
            me.Start();
            me.Run().Wait();
            me.Finish();
        }

        private void Initialize()
        {
            this._cts = new CancellationTokenSource();
            this._errQueue = new BlockingCollection<Exception>();
            this._msgQueue = new BlockingCollection<Message>();

            this.StartErrorReporter();
            this.StartMessageReporter();
            this.StartStopper();
        }

        private void StartErrorReporter()
        {
            this._errorReporter = new Thread(() =>
            {
                foreach (var item in this._errQueue.GetConsumingEnumerable())
                {
                    Console.WriteLine(item);
                }

                this._msgQueue.Add(new Message {Value = $"[ERROR] End"});
            }) {IsBackground = true, Name = "ErrorReporter"};

            this._errorReporter.Start();
            this._msgQueue.Add(new Message {Value = $"[ERROR] Start"});
        }

        private void StartMessageReporter()
        {
            this._messageReporter = new Thread(() =>
            {
                foreach (var item in this._msgQueue.GetConsumingEnumerable())
                {
                    Console.WriteLine($"[MSG] {item}");
                }

                // このタイミングでは _msgQueue は IsCompleted 状態となっているので利用できない
                Console.WriteLine("[REPORT] End");
            }) {IsBackground = true, Name = "MessageReporter"};

            this._messageReporter.Start();
            this._msgQueue.Add(new Message {Value = $"[REPORT] Start"});
        }

        private void StartStopper()
        {
            this._stopper = new Thread(() =>
            {
                try
                {
                    while (true)
                    {
                        var userInput = Console.ReadLine();
                        if (string.IsNullOrWhiteSpace(userInput))
                        {
                            continue;
                        }

                        if (userInput.ToLower() == "quit")
                        {
                            this._msgQueue.Add(new Message {Value = $"[STOPPER] ** quit **"});
                            this._cts.Cancel();
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    this._errQueue.Add(ex);
                }
                finally
                {
                    this._msgQueue.Add(new Message {Value = $"[STOPPER] End"});
                }
            }) {IsBackground = true, Name = "Stopper"};

            this._stopper.Start();
            this._msgQueue.Add(new Message {Value = $"[STOPPER] Start"});
        }

        private void Start()
        {
            // TCP/UDPのポート 49152 から 65535 までは公式に未アサインとなっているポート
            // 利用する場合はここからポート番号を利用した方が間違いが少ない
            var ip = IPAddress.Parse("127.0.0.1");
            var port = 50001;
            var endpoint = new IPEndPoint(ip, port);

            this._listener = new TcpListener(endpoint);
            this._cts.Token.Register(() =>
            {
                this._msgQueue.Add(new Message {Value = $"[SERVER] ** STOP **"});
                this._listener.Stop();
            });
            this._listener.Start();

            this._msgQueue.Add(new Message {Value = $"[SERVER] Start"});
        }

        private async Task Run()
        {
            try
            {
                while (!this._cts.IsCancellationRequested)
                {
                    this._msgQueue.Add(new Message {Value = $"[SERVER] Accepting...."});
                    try
                    {
                        this.Accept(await this._listener.AcceptTcpClientAsync());
                    }
                    catch (Exception ex)
                    {
                        // 上位でキャンセル処理が発行された
                        this._msgQueue.Add(new Message {Value = $"[SERVER] ** Cancel Accept with ({ex.Message}) **"});
                    }
                }
            }
            finally
            {
                this._listener.Stop();
            }
        }

        // Fire and Forget task pattern.
        // https://ufcpp.wordpress.com/2012/11/12/asyncawait%E3%81%A8%E5%90%8C%E6%99%82%E5%AE%9F%E8%A1%8C%E5%88%B6%E5%BE%A1/
        private async void Accept(TcpClient client)
        {
            await Task.Yield();
            try
            {
                this._msgQueue.Add(new Message {Value = $"[SERVER] Accepted"});
                using (client)
                {
                    using (var stream = client.GetStream())
                    {
                        var queue = new BlockingCollection<byte[]>();
                        var ns = stream;
                        var recvTask = Task.Run(async () =>
                        {
                            await Task.Yield();
                            this._msgQueue.Add(new Message {Value = $"[SERVER] Start Recv Task"});

                            while (!this._cts.IsCancellationRequested)
                            {
                                var buf = new byte[23];
                                var bytesRead = 0;
                                var chunkSize = 1;

                                Exception err = null;
                                while (bytesRead < buf.Length && chunkSize > 0)
                                {
                                    try
                                    {
                                        bytesRead += chunkSize =
                                            await ns.ReadAsync(buf, bytesRead, buf.Length - bytesRead);
                                    }
                                    catch (IOException ioEx)
                                    {
                                        err = ioEx;
                                        this._errQueue.Add(ioEx);
                                        break;
                                    }
                                }

                                if (bytesRead == 0 && chunkSize == 0)
                                {
                                    this._msgQueue.Add(new Message {Value = $"[RECV] client close detected"});
                                    break;
                                }

                                if (err != null)
                                {
                                    this._msgQueue.Add(new Message {Value = $"[RECV] recv error."});
                                    break;
                                }

                                queue.Add(buf);
                                this._msgQueue.Add(new Message {Value = $"[RECV] {bytesRead} bytes"});
                            }

                            queue.CompleteAdding();
                            this._msgQueue.Add(new Message {Value = $"[SERVER] End Recv Task"});
                        });

                        var sendTask = Task.Run(async () =>
                        {
                            await Task.Yield();
                            this._msgQueue.Add(new Message {Value = $"[SERVER] Start Send Task"});

                            var rnd = new Random();
                            foreach (var item in queue.GetConsumingEnumerable())
                            {
                                var delay = rnd.Next(1000, 2000);
                                this._msgQueue.Add(new Message {Value = $"[SEND] {delay}ms sleep"});
                                await Task.Delay(delay);

                                try
                                {
                                    await ns.WriteAsync(item, 0, item.Length);
                                    await ns.FlushAsync();
                                }
                                catch (IOException ioEx)
                                {
                                    if (ioEx.InnerException is SocketException sockEx)
                                    {
                                        this._msgQueue.Add(new Message
                                            {Value = $"[SEND] write error with {sockEx.Message}"});
                                        break;
                                    }

                                    this._errQueue.Add(ioEx);
                                }

                                this._msgQueue.Add(new Message {Value = $"[SEND] {item.Length} bytes"});
                            }

                            this._msgQueue.Add(new Message {Value = $"[SERVER] End Send Task"});
                        });

                        await Task.WhenAll(recvTask, sendTask);
                    }
                }
            }
            catch (Exception ex)
            {
                this._errQueue.Add(ex);
            }
        }

        private void Finish()
        {
            this._msgQueue.Add(new Message {Value = $"[Main] Finish start"});
            this._stopper?.Join();
            this._errQueue?.CompleteAdding();
            this._errorReporter?.Join();
            this._msgQueue?.CompleteAdding();
            this._messageReporter?.Join();
            Console.WriteLine("[Main] Finish End");
        }
    }
}