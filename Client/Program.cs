using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
// ReSharper disable RedundantStringInterpolation

namespace Client
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
        
        private CancellationTokenSource _cts;
        private TcpClient _client;

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
            this._client = new TcpClient();
            
            this._cts = new CancellationTokenSource();
            this._cts.Token.Register(() => this._client.Close());
            
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

        private void Start()
        {
            var ip = IPAddress.Parse("127.0.0.1");
            var port = 50001;
            var endpoint = new IPEndPoint(ip, port);

            this._client.Connect(endpoint);
            this._msgQueue.Add(new Message() {Value=$"[CLIENT] Start"});
        }

        private async Task Run()
        {
            await Task.Yield();
            try
            {
                while (!this._cts.IsCancellationRequested)
                {
                    using (var stream = this._client.GetStream())
                    {
                        var ns = stream;
                        var sendTask = Task.Run(async () =>
                        {
                            await Task.Yield();
                            this._msgQueue.Add(new Message {Value = $"[CLIENT] Start Send Task"});
                            
                            var rnd = new Random();
                            while (!this._cts.IsCancellationRequested)
                            {
                                var d = DateTime.Now;
                                var s = d.ToString("yyyy/MM/dd HH:mm:ss.fff");
                                var b = Encoding.UTF8.GetBytes(s);

                                try
                                {
                                    await ns.WriteAsync(b, 0, b.Length);
                                    await ns.FlushAsync();
                                }
                                catch (IOException ioEx)
                                {
                                    this._errQueue.Add(ioEx);
                                    break;
                                }
                                
                                this._msgQueue.Add(new Message {Value = $"[SEND] {b.Length} bytes"});
                                
                                var delay = rnd.Next(1000, 3000);
                                this._msgQueue.Add(new Message {Value = $"[SEND] {delay}ms sleep"});
                                await Task.Delay(delay);
                            }
                            
                            this._msgQueue.Add(new Message {Value = $"[CLIENT] End Send Task"});
                        });

                        var recvTask = Task.Run(async () =>
                        {
                            await Task.Yield();
                            this._msgQueue.Add(new Message {Value = $"[CLIENT] Start Recv Task"});
                            
                            while (!this._cts.IsCancellationRequested)
                            {
                                var buf = new byte[23];
                                int bytesRead = 0;
                                int chunkSize = 1;

                                while (bytesRead < buf.Length && chunkSize > 0)
                                {
                                    try
                                    {
                                        bytesRead += chunkSize =
                                            await ns.ReadAsync(buf, bytesRead, buf.Length - bytesRead);
                                    }
                                    catch (IOException ioEx)
                                    {
                                        if (ioEx.InnerException is SocketException sockEx)
                                        {
                                            if (sockEx.Message.ToLower().Contains("operation canceled"))
                                            {
                                                this._msgQueue.Add(new Message{Value=$"socket cancel"});
                                            }
                                        }
                                        else
                                        {
                                            this._errQueue.Add(ioEx);                                            
                                        }
                                        
                                        break;
                                    }
                                }
                                
                                // 対向先が切断されたかどうかの判定は以下のやり方もあるみたい
                                // (1) https://stackoverflow.com/questions/15067014/c-sharp-detecting-tcp-disconnect#comment21195361_15067180
                                // (2) https://stackoverflow.com/a/44242399
                                // 
                                // (2) のやり方が一番手堅い感じがしている
                                if (bytesRead == 0 && chunkSize == 0)
                                {
                                    this._msgQueue.Add(new Message {Value = $"[RECV] server close detected"});
                                    break;
                                }
                                
                                this._msgQueue.Add(new Message {Value = $"[RECV] {bytesRead} bytes"});
                            }
                            
                            this._msgQueue.Add(new Message {Value = $"[CLIENT] End Recv Task"});
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