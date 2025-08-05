using NAudio;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NDesk.Options;
using Rug.Osc;
using System;
using System.Diagnostics.Metrics;
using System.Net;
using System.Runtime.CompilerServices;
using VRC.OSCQuery;

namespace VRCYapInterceptor
{
    class YapInterceptor
    {
        static class OSCAddresses
        {
            //public static string INTERCEPTOR_ENABLED = "/avatar/parameters/YapInterceptor/Enabled";
            public static string INTERCEPTOR_ENABLED = "/avatar/parameters/VoiceReplace/Enabled";
            //public static string INTERCEPTOR_INTERCEPTING = "/avatar/parameters/YapInterceptor/Intercepting";
            public static string INTERCEPTOR_INTERCEPTING = "/avatar/parameters/VoiceReplace/Replacing";
            public static string INPUT_VOICE = "/input/Voice";
            public static string INPUT_JUMP = "/input/Jump";
            public static string AVATAR_MUTESELF = "/avatar/parameters/MuteSelf";
            public static string AVATAR_VOICE = "/avatar/parameters/Voice";
        }

        private static YapInterceptor? _instance = null;
        public static YapInterceptor Instance
        {
            get
            {
                if (_instance == null) { _instance = new YapInterceptor(); }
                return _instance;
            }
        }

        public int tcpPort, udpPort;
        public OSCQueryService qsvc;
        public OscListener listener;
        public OscSender senderToVrc;

        public bool state_isEnabled = true; // whether we'll do things
        public bool state_isMuted = false;
        public bool state_isActive = false; // whether we're actively muting

        public WaveInEvent waveIn;

        public int windowSize;
        public int miniWindowSize;
        public Queue<float> window;
        public Queue<float> miniWindow;
        public float average;
        public float miniMax;
        public float startThreshold, stopThreshold;

        public YapInterceptor()
        {
            //this.existingAddresses = new HashSet<string>();
            this.tcpPort = Extensions.GetAvailableTcpPort();
            this.udpPort = Extensions.GetAvailableUdpPort();
            this.qsvc = new OSCQueryServiceBuilder()
                .WithTcpPort(this.tcpPort)
                .WithUdpPort(this.udpPort)
                .WithServiceName("VRC Yap Interceptor")
                .WithDefaults()
                //.StartHttpServer()
                //.AdvertiseOSCQuery()
                //.AdvertiseOSC()
                .Build();

            this.qsvc.AddEndpoint<bool>(OSCAddresses.INTERCEPTOR_ENABLED, Attributes.AccessValues.WriteOnly);
            this.qsvc.AddEndpoint<bool>(OSCAddresses.INTERCEPTOR_INTERCEPTING, Attributes.AccessValues.ReadOnly);
            this.qsvc.AddEndpoint<bool>(OSCAddresses.INPUT_VOICE, Attributes.AccessValues.ReadOnly);
            this.qsvc.AddEndpoint<bool>(OSCAddresses.INPUT_JUMP, Attributes.AccessValues.ReadOnly);
            this.qsvc.AddEndpoint<bool>(OSCAddresses.AVATAR_MUTESELF, Attributes.AccessValues.WriteOnly);
            //this.qsvc.AddEndpoint<bool>("/avatar/parameters/Voice", Attributes.AccessValues.WriteOnly);

            this.listener = new OscListener(this.udpPort);
            this.senderToVrc = new OscSender(IPAddress.Loopback, 0, 9000);

            this.listener.Attach(OSCAddresses.INTERCEPTOR_ENABLED, OSC_OnReceiveEnabled);
            this.listener.Attach(OSCAddresses.AVATAR_MUTESELF, OSC_OnReceiveMuted);

            this.waveIn = new WaveInEvent
            {
                DeviceNumber = 0,
                WaveFormat = new WaveFormat(rate: 44100, bits: 16, channels: 1),
                BufferMilliseconds = 20
            };
            this.waveIn.DataAvailable += WaveIn_OnWaveData;


        }


        //public OSCQueryService oscq;

        static void Main(string[] args)
        {
            int windowSize = 32;
            int miniWindowSize = 4;
            float startThreshold = 0.7f;
            float stopThreshold = 0.5f;

            var p = new OptionSet()
            {
                { "window=", v => windowSize = int.Parse(v) },
                { "startThreshold=", v => startThreshold = float.Parse(v) },
                { "stopThreshold=", v => stopThreshold = float.Parse(v) },
            };

            var extra = p.Parse(args);
            //var oscq = new OSCQueryService();
            //oscq.ServerName = "VRC Yap Interceptor";
            //oscq.AddEndpoint<bool>("/avatar/parameters/YapInterceptor/Enabled", Attributes.AccessValues.WriteOnly);
            //oscq.AddEndpoint<bool>("/avatar/parameters/YapInterceptor/Intercepting", Attributes.AccessValues.ReadOnly);
            //oscq.AddEndpoint<bool>("/input/Voice", Attributes.AccessValues.ReadOnly);
            //oscq.AddEndpoint<bool>("/input/Jump", Attributes.AccessValues.ReadOnly);
            //oscq.AddEndpoint<bool>("/avatar/parameters/MuteSelf", Attributes.AccessValues.WriteOnly);
            //oscq.AddEndpoint<bool>("/avatar/parameters/Voice", Attributes.AccessValues.WriteOnly);

            Console.WriteLine("Hello, World!");
            var instance = Instance;
            instance.windowSize = windowSize;
            //instance.window = new float[instance.windowSize];
            instance.window = new Queue<float>();
            for (int i = 0; i < windowSize; ++i)
            {
                instance.window.Enqueue(0f);
            }
            instance.miniWindow = new Queue<float>();
            for (int i = 0; i < miniWindowSize; ++i)
            {
                instance.miniWindow.Enqueue(0f);
            }
            instance.average = 0f;
            instance.miniMax = 0f;
            instance.startThreshold = startThreshold;
            instance.stopThreshold = stopThreshold;

            instance.Start();
            Console.ReadKey();
            instance.Cleanup();
        }

        public void Update(float nextValue)
        {
            float miniLeaving = this.miniWindow.Dequeue();
            this.miniWindow.Enqueue(nextValue);
            float nextMax = this.miniWindow.Max();
            float leaving = this.window.Dequeue();
            this.window.Enqueue(nextMax);
            this.average -= (leaving / (float)this.windowSize);
            this.average += (nextMax / (float)this.windowSize);

            // print a level meter using the console
            int barNextSize = (int)(nextMax * 70);
            int barAvgSize = (int)(this.average * 70);
            string bar = new('#', barNextSize);
            if (barAvgSize > barNextSize)
            {
                int diff = barAvgSize - barNextSize;
                bar = $"{bar}{new('-', diff - 1)}|";
            }
            string meter = "[" + bar.PadRight(70, '-') + "]";
            Console.SetCursorPosition(0, 3);
            //Console.CursorLeft = 0;
            Console.CursorVisible = false;
            Console.WriteLine($"{meter} {nextMax * 100:00.0}% ({this.average * 100:00.0}%)");
            Console.Write($"YOU ARE [{(this.state_isActive ? "DEF" : "NOT")}] YAPPING // System enabled: {(this.state_isEnabled ? 1 : 0)} // Player muted: {(this.state_isMuted ? 1 : 0)}");


            if (this.state_isActive)
            {
                if (this.average < this.stopThreshold || !this.state_isEnabled)
                {
                    this.Disable();
                }
            }
            else
            {
                if (this.average > this.startThreshold && this.state_isEnabled && !this.state_isMuted)
                {
                    this.Enable();
                }
            }
        }

        public string GetConsoleHeader()
        {
            return $"VRChat Yap Interceptor\nListening on {this.udpPort}\n(press any key to exit)";
        }

        public void Start()
        {
            //this.senderToVrc.Connect();
            this.senderToVrc.Connect();
            this.senderToVrc.Send(new OscMessage(OSCAddresses.INPUT_JUMP, true));
            Thread.Sleep(10);
            this.senderToVrc.Send(new OscMessage(OSCAddresses.INPUT_JUMP, false));
            this.listener.Connect();
            this.waveIn.StartRecording();
            Console.WriteLine(GetConsoleHeader());
        }

        public void Enable()
        {
            this.state_isActive = true;
            this.senderToVrc.Send(new OscMessage(OSCAddresses.INTERCEPTOR_INTERCEPTING, true));
            this.senderToVrc.Send(new OscMessage(OSCAddresses.INPUT_VOICE, true));
            Thread.Sleep(10);
            this.senderToVrc.Send(new OscMessage(OSCAddresses.INPUT_VOICE, false));
            //this.senderToVrc.Send()
        }

        public void Disable()
        {
            this.state_isActive = false;
            this.senderToVrc.Send(new OscMessage(OSCAddresses.INTERCEPTOR_INTERCEPTING, false));
            this.senderToVrc.Send(new OscMessage(OSCAddresses.INPUT_VOICE, true));
            Thread.Sleep(10);
            this.senderToVrc.Send(new OscMessage(OSCAddresses.INPUT_VOICE, false));
        }

        static void WaveIn_OnWaveData(object? sender, WaveInEventArgs e)
        {
            // copy buffer into an array of integers
            Int16[] values = new Int16[e.Buffer.Length / 2];
            Buffer.BlockCopy(e.Buffer, 0, values, 0, e.Buffer.Length);

            // determine the highest value as a fraction of the maximum possible value
            float fraction = (float)values.Max() / 32768;


            Instance.Update(fraction);
        }

        public void OSC_OnReceiveEnabled(OscMessage msg)
        {
            this.state_isEnabled = (bool)msg.First();
        }

        public void OSC_OnReceiveMuted(OscMessage msg)
        {
            this.state_isMuted = (bool)msg.First();
        }

        public void Cleanup()
        {
            this.listener.Dispose();
            this.senderToVrc.Dispose();
            this.qsvc.Dispose();
        }
    }
}