using System;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Tesseract;
using WebSocketSharp;
using WebSocketSharp.Net;
using WebSocketSharp.Server;

namespace TSWOCR_WS {
    class WebsocketProcessor : WebSocketBehavior {
        public static double speed = 0;
        public static double distance = 0;
        public static double gradient = 0;
        public static double speedLimitDistance = 0;
        public static double speedLimitValue = 0;
        public static int signalState = 0;
        public static double signalValue = 0;
        public static int stationState = -1;
        public static int throttleLockState = 0;
        public static double speedLimitRatio = 0;

        private static TcpClient autopilotSocket = null;
        private static BinaryWriter autopilotWriter = null;

        public static event EventHandler ResetRequested;

        public WebsocketProcessor() {

        }

        protected override void OnMessage(MessageEventArgs e) {
            if (e.Data == "reset") {
                ResetRequested(null, null);
            } else if (e.Data.StartsWith("ap")) {
                // prepare socket first
                var apCommand = e.Data.Substring(2);

                var sendSuc = false;
                do {
                    try {
                        if (autopilotSocket == null) throw new Exception();

                        autopilotWriter.Write(apCommand);
                        Console.WriteLine(apCommand);

                        sendSuc = true;
                    } catch {
                        try {
                            autopilotSocket = new TcpClient("127.0.0.1", 4776);
                            autopilotWriter = new BinaryWriter(autopilotSocket.GetStream());
                        } catch {
                            // give up
                            sendSuc = true;
                        }
                    }
                } while (!sendSuc);
            } else {
                Send(speed + ";" + distance + ";" + gradient + ";" + speedLimitDistance + ";" + speedLimitValue + ";" + signalState + ";" + signalValue + ";" + throttleLockState + ";" + speedLimitRatio);
                if (stationState >= 0) {
                    Send("station;" + stationState);
                }
            }
        }
    }
    class Program {
        static long lastTick;
        public static TesseractEngine CreateEngine(string whitelist, string lang = "eng", PageSegMode psm = PageSegMode.SingleLine) {
            TesseractEngine engine = new TesseractEngine("tessdata", lang);
            if (whitelist.Length > 0)
                engine.SetVariable("tessedit_char_whitelist", whitelist);
            engine.SetVariable("tessedit_ocr_engine_mode", "0");
            engine.DefaultPageSegMode = psm;
            engine.SetVariable("debug_file", "NUL");
            return engine;
        }
        static void Main(string[] args) {
            RawDataProcessor processor = new RawDataProcessor();

            WebsocketProcessor.ResetRequested += (sender, e) => {
                Console.WriteLine("Reset the speed and distance");
                processor.Reset();
            };

            lastTick = DateTime.Now.Ticks;

            var sv = new HttpServer(4775);
            sv.DocumentRootPath = "./web";
            sv.DocumentRootPath = "C:\\Users\\User\\Documents\\TSWOCR Web\\dist";
            sv.AddWebSocketService<WebsocketProcessor>("/");

            // Set the HTTP GET request event.
            sv.OnGet += (sender, e) => {
                var req = e.Request;
                var res = e.Response;

                var path = req.RawUrl;

                if (path == "/")
                    path += "index.html";

                byte[] contents;

                if (!e.TryReadFile(path, out contents)) {
                    res.StatusCode = (int)HttpStatusCode.NotFound;

                    return;
                }

                if (path.EndsWith(".html")) {
                    res.ContentType = "text/html";
                    res.ContentEncoding = Encoding.UTF8;
                } else if (path.EndsWith(".js")) {
                    res.ContentType = "application/javascript";
                    res.ContentEncoding = Encoding.UTF8;
                }

                res.ContentLength64 = contents.LongLength;

                res.Close(contents, true);
            };

            sv.Start();

            var delay = 0;

            // Train Supervision Reading
            new Thread(() => {
                while (true) {
                    processor.ReadTrainSupervision(out double spdLimDist, out double spdLimVal, out int signal, out double signalDist, out int throttleLock);

                    WebsocketProcessor.speedLimitValue = spdLimVal;
                    WebsocketProcessor.speedLimitDistance = spdLimDist;
                    WebsocketProcessor.signalState = signal;
                    WebsocketProcessor.signalValue = signalDist;
                    WebsocketProcessor.throttleLockState = throttleLock;

                    //Console.WriteLine("SUP " + spdLimVal + " in " + spdLimDist + " / " + signal + "s in " + signalDist);

                    Thread.Sleep(delay);
                }
            }) {
                IsBackground = false
            }.Start();

            // Train SpeedLimit Reading
            new Thread(() => {
                while (true) {
                    ScreenImageProcessor.ReadSpeedLimitInGauge(out double ratio);

                    WebsocketProcessor.speedLimitRatio = ratio;
                    Console.WriteLine(WebsocketProcessor.speedLimitRatio);
                    if (delay == 0) {
                        Thread.Sleep(500);
                    } else {
                        Thread.Sleep(1500);
                    }
                }
            }) {
                IsBackground = false
            }.Start();

            // Train Position Reading
            while (true) {
                processor.ReadTrainState(out double speed, out double distance, out int gradient);

                var mpsSpeed = speed / 3.6;

                //if (speed < 1) continue;

                double meters = distance;

                //Console.WriteLine("POS " + speed + " -> " + Math.Round(distance));

                if (speed <= 0) {
                    int ss = processor.SSGetStationState();
                    WebsocketProcessor.stationState = ss;
                } else {
                    WebsocketProcessor.stationState = -1;
                }

                WebsocketProcessor.gradient = gradient;

                if (speed == 0 && distance == 0) { // stopped
                    delay = 1000;
                    WebsocketProcessor.speed = 0;
                    WebsocketProcessor.distance = -1;

                    Thread.Sleep(1000);
                    continue;
                } else {
                    delay = 0;
                }

                if (/*speed < 10 || */distance == 0) {
                    WebsocketProcessor.speed = 0;
                    WebsocketProcessor.distance = -1;
                }

                WebsocketProcessor.speed = speed;
                WebsocketProcessor.distance = distance;

                Thread.Sleep(delay);
            }
        }

    }

    static class ProcessorUtils {
        public static Bitmap Screenshot(Rectangle bounds) {
            var bm = new Bitmap(bounds.Width, bounds.Height);
            using (Graphics g = Graphics.FromImage(bm)) {
                g.CopyFromScreen(new Point(bounds.Left, bounds.Top), Point.Empty, bounds.Size);
            }
            return bm;
        }

        public static void OptimiseForOCR(Bitmap image, int maxColor) {
            for (var y = 0; y < image.Height; y++) {
                for (var x = 0; x < image.Width; x++) {
                    Color inv = image.GetPixel(x, y);

                    if (inv.R < maxColor || inv.G < maxColor || inv.B < maxColor) {
                        image.SetPixel(x, y, Color.White);
                        continue;
                    }

                    image.SetPixel(x, y, Color.FromArgb(inv.ToArgb() ^ 0xffffff));
                }
            }
        }

        public static double ColorDist(Color a, Color b) {
            return Math.Sqrt(Math.Pow(a.R - b.R, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.G - b.G, 2));
        }
    }

    static class ScreenImageProcessor {
        private const int RADIUS = 184;
        private static double GaugeAngle(int x, int y) {
            var c = Math.Atan2(y, x);
            if (c > Math.PI / 2) c -= Math.PI / 2;
            else c += Math.PI * 3 / 2;
            return c;
        }

        private static readonly double ZERO_ANGLE = GaugeAngle(-171, 68);
        private static readonly double MAX_ANGLE = GaugeAngle(171, 71);
        private static readonly double ANGLE_SIZE = MAX_ANGLE - ZERO_ANGLE;

        private static double getRawAngle(double vRatio) {
            return (ANGLE_SIZE * vRatio) + ZERO_ANGLE + Math.PI / 2;
        }

        private static int lastIndexValue = 0;
        private static readonly double IndexDividedBy = 2000;

        public static void ReadSpeedLimitInGauge(out double ratio) {
            // First, see whether teh speed limit changed or not
            {
                var a = getRawAngle(lastIndexValue / IndexDividedBy);
                using (var b = ProcessorUtils.Screenshot(new Rectangle(2089 + (int)Math.Round(Math.Cos(a) * RADIUS), 1194 + (int)Math.Round(Math.Sin(a) * RADIUS), 1, 1))) {
                    if (ProcessorUtils.ColorDist(b.GetPixel(0, 0), Color.Red) < 80) {
                        ratio = lastIndexValue / IndexDividedBy;
                        return;
                    }
                }
            }
            Rectangle bounds = new Rectangle(1897, 1003, 384, 276);
            // center point becomes: (2089 - 1897, 1194 - 1003) = (192, 191)
            int cx = 192;
            int cy = 191;
            using (var b = ProcessorUtils.Screenshot(bounds)) {
                var a = getRawAngle(lastIndexValue / IndexDividedBy);
                var co = b.GetPixel(cx + (int)Math.Round(Math.Cos(a) * RADIUS), cy + (int)Math.Round(Math.Sin(a) * RADIUS));
                if (ProcessorUtils.ColorDist(co, Color.Red) < 80) {
                    ratio = lastIndexValue / IndexDividedBy;
                    return;
                }

                int cnt = 0;

                for (int i = 0; i <= IndexDividedBy; i++) {
                    a = getRawAngle(i / IndexDividedBy);
                    co = b.GetPixel(cx + (int)Math.Round(Math.Cos(a) * RADIUS), cy + (int)Math.Round(Math.Sin(a) * RADIUS));
                    if (ProcessorUtils.ColorDist(co, Color.Red) < 80) {
                        cnt++;
                    } else {
                        lastIndexValue = i - cnt / 2;
                        ratio = lastIndexValue / IndexDividedBy;
                        if (cnt > 0) return;
                    }
                }
            }
            ratio = -1;
            lastIndexValue = 0;
        }
    }

    class RawDataProcessor {
        private TesseractEngine stationOCR;
        private TesseractEngine speedOCR;
        private TesseractEngine planOCR;
        private TesseractEngine kmhOCR;
        private TesseractEngine gameMissionOCR;
        private TesseractEngine digitalOCR;
        public RawDataProcessor() {
            stationOCR = Program.CreateEngine("0123456789.km", "deu", PageSegMode.SingleLine);
            speedOCR = Program.CreateEngine("0123456789.", "deu", PageSegMode.SparseText);
            planOCR = Program.CreateEngine("0123456789.km", "deu", PageSegMode.SingleLine);
            kmhOCR = Program.CreateEngine("KMHkm/h", "deu", PageSegMode.SingleLine);
            gameMissionOCR = Program.CreateEngine("", "eng", PageSegMode.SingleLine);
            digitalOCR = Program.CreateEngine("0123456789.%", "deu", PageSegMode.SingleWord);
        }

        private double prevSpeed = 0;
        private double prevDetectedDistance = 0;

        private double prevPrevKiloDistance = 0;
        private double prevKiloDistance = 0;
        private double lastKiloFactor = 1;
        private double emulatedKiloDistance = 0;

        private long lastDataCall = CurrentMilliseconds();
        private long lastSpeedValid = 0;
        private long lastDistValid = 0;

        private int anotherValidCount = 0;
        private double anotherDist = 0;

        private static long CurrentMilliseconds() {
            return DateTime.Now.Ticks / 10000;
        }

        public void Reset() {
            prevSpeed = 0;
            prevDetectedDistance = 0;
            anotherDist = 0;
            anotherValidCount = 0;

            lastSpeedValid = 0;
            lastDistValid = 0;

            prevPrevKiloDistance = 0;
            prevKiloDistance = 0;
            emulatedKiloDistance = 0;
            lastKiloFactor = 1;
        }

        public void ReadTrainState(out double speed, out double distance, out int gradient) {
            var currentTime = CurrentMilliseconds(); // in ms
            var callTimeGap = currentTime - lastDataCall;

            // Read raw data from screen
            var speedData = OCRSpeedRead();
            var distanceData = OCRDistanceAndIsKiloRead();
            double? distanceMeters;
            bool isKilo;
            // Retrieve remaining distance in meters
            if (distanceData != null) {
                isKilo = distanceData.Value.Item2;
                distanceMeters = distanceData.Value.Item1 * (isKilo ? 1000 : 1);
            } else {
                distanceMeters = null;
                isKilo = false;
            }

            //Console.WriteLine(speedData + " " + distanceData);

            double finalSpeed;
            if (speedData != null) {
                if (0 < prevSpeed && prevSpeed < 15 && speedData > 10 && Math.Abs(((speedData ?? 0) * 0.1) - prevSpeed) < Math.Abs((speedData ?? 0) - prevSpeed)/* && (distanceMeters ?? 0) > 0*/) {
                    finalSpeed = (speedData ?? 0) / 10.0;
                } else if (0 < prevSpeed && prevSpeed < 2 && speedData < 10 && speedData > 1 && Math.Abs(((speedData ?? 0) / 10.0) - prevSpeed) < Math.Abs((speedData ?? 0) - prevSpeed)/* && (distanceMeters ?? 0) > 0*/) {
                    finalSpeed = (speedData ?? 0) / 10.0;
                } else {
                    finalSpeed = speedData ?? 0;
                }
                lastSpeedValid = CurrentMilliseconds();
            } else {
                finalSpeed = prevSpeed;
            }

            bool distanceValid = ValidateDistance(distanceMeters, finalSpeed, callTimeGap, isKilo, prevDetectedDistance, prevSpeed);
            bool distanceDiv10Valid = ValidateDistance(distanceMeters / 10.0, finalSpeed, callTimeGap, isKilo, prevDetectedDistance, prevSpeed);
            bool properDistanceFound = false;

            double memoryDistance;
            if (distanceValid) {
                memoryDistance = distanceMeters ?? 0;
                anotherValidCount = 0;
                properDistanceFound = true;
                //} else if (distanceDiv10Valid && isKilo && distanceMeters / 10.0 >= 1000) {
                //    memoryDistance = distanceMeters / 10.0 ?? 0;
                //    anotherValidCount = 0;
                //    properDistanceFound = true;
            } else {
                var avgSpd = (prevSpeed + finalSpeed) * 0.5;
                var estimateDist = (avgSpd / 3.6) * (callTimeGap / 1000.0);
                memoryDistance = Math.Abs(prevDetectedDistance - estimateDist);

                if (ValidateDistance(distanceMeters, finalSpeed, callTimeGap, isKilo, anotherDist, prevSpeed)) {
                    anotherValidCount++;
                } else {
                    anotherValidCount = 0;
                }
                if (anotherValidCount > 10) {
                    memoryDistance = anotherDist;
                }
            }

            double finalDistance = memoryDistance;

            if (properDistanceFound && isKilo) {
                if (prevKiloDistance != memoryDistance) {
                    var diff = prevKiloDistance - memoryDistance;
                    if (prevPrevKiloDistance != 0 && prevKiloDistance != 0 && (diff == 1000 || diff == 100 || prevKiloDistance == 0)) {
                        var distanceDiff = prevPrevKiloDistance - emulatedKiloDistance;
                        Console.WriteLine(distanceDiff + ", " + diff + ", " + Math.Round(lastKiloFactor * 100) / 100.0);
                        lastKiloFactor /= distanceDiff / diff;
                        if (lastKiloFactor < 0.1) lastKiloFactor = 1;
                        emulatedKiloDistance = prevKiloDistance;
                    }
                    prevPrevKiloDistance = prevKiloDistance;
                    prevKiloDistance = memoryDistance;
                }
                if (emulatedKiloDistance != 0) {
                    emulatedKiloDistance -= ((finalSpeed / 3.6) * (callTimeGap / 1000.0)) * lastKiloFactor;
                    finalDistance = Math.Max(memoryDistance, emulatedKiloDistance);
                }
            }

            if (finalSpeed == 0) {
                anotherValidCount = 0;
                prevKiloDistance = 0;
                prevPrevKiloDistance = 0;
                emulatedKiloDistance = 0;
                lastKiloFactor = 1;
                memoryDistance = 0;
                finalDistance = 0;
            }

            // read gradient
            var v = OCRGradientRead();
            if (v == null) {
                gradient = -100;
            } else {
                gradient = (int)v;
            }

            // Data output

            lastDataCall = currentTime;
            prevSpeed = speed = finalSpeed;
            prevDetectedDistance = memoryDistance;
            distance = finalDistance;
            anotherDist = distanceMeters ?? 0;
        }

        public void ReadTrainSupervision(out double spdLimDist, out double spdLimValue, out int signal, out double signalDist, out int throttleLock) {
            // Signal data detection
            var signalState = SSGetSignalState();
            signal = signalState;
            if (signalState < 0) {
                signalDist = -1;
            } else {
                var signalDistanceData = OCRSpdLimDistanceAndIsKiloRead(-67);
                if (signalDistanceData != null) {
                    var dd = signalDistanceData.Value.Item1 * (signalDistanceData.Value.Item2 ? 1000 : 1);
                    signalDist = dd;
                } else {
                    signalDist = -1;
                }
            }

            // Speed limit detection
            var speedLimitOffset = -1;

            // Speed limit appears at signal position if signal doesn't exist
            if (signalState == -1 && OCRSpdLimExists(-67)) {
                speedLimitOffset = -67;
            } else if (OCRSpdLimExists()) {
                speedLimitOffset = 0;
            }

            if (speedLimitOffset == -1) {
                spdLimDist = -1;
                spdLimValue = -1;
            } else {
                var speedLimitDistanceData = OCRSpdLimDistanceAndIsKiloRead(speedLimitOffset);
                var speedLimitValueData = OCRSpdLimSpeedRead(speedLimitOffset);
                if (speedLimitDistanceData != null) {
                    var dd = speedLimitDistanceData.Value.Item1 * (speedLimitDistanceData.Value.Item2 ? 1000 : 1);
                    spdLimDist = dd;
                } else {
                    spdLimDist = -1;
                }
                if (speedLimitValueData != null) {
                    spdLimValue = speedLimitValueData.Value;
                } else {
                    spdLimValue = -1;
                }
            }

            throttleLock = SSGetThrottleLockState() ? 1 : 0;
        }

        private bool ValidateDistance(double? rawMetersRemain, double currentV, long dataCallInterval, bool isKilo, double prevDist, double prevSpeed) {
            var distanceValid = true;

            if (rawMetersRemain != null) {
                var data = rawMetersRemain ?? 0;

                if (isKilo) {
                    if (Math.Abs(data * 0.0001 - prevDist) < Math.Abs(data - prevSpeed)) {
                        distanceValid = false;
                    }
                } else {
                    // if actual moved distance is much higher than estimated, it is probably wrong
                    if (Math.Abs(data - prevDist) > /*estimate move distance: */Math.Max(currentV, 20d) / 3.6 /*get m/s*/ * dataCallInterval / 1000.0 * 2.0 /* tolerance */ && currentV > 0) {
                        distanceValid = false;
                    }
                }

                if (prevDist < 1 && data != 0) {
                    distanceValid = true;
                }
            } else {
                distanceValid = false;
            }
            return distanceValid;
        }

        #region Station and speed OCR
        private double? OCRSpeedRead() {
            //Rectangle speedBounds = new Rectangle(2070, 1235, 120, 40);
            Rectangle speedBounds = new Rectangle(2030, 1197, 116, 70);

            string rawSpeed;
            double speed;

            using (Bitmap bitmap = ProcessorUtils.Screenshot(speedBounds)) {
                for (var y = 0; y < bitmap.Height; y++) {
                    for (var x = 0; x < bitmap.Width; x++) {
                        Color inv = bitmap.GetPixel(x, y);

                        if ((inv.R < 230 || inv.G < 230 || inv.B < 230) && ProcessorUtils.ColorDist(inv, Color.FromArgb(240, 101, 118)) > 50 && ProcessorUtils.ColorDist(inv, Color.FromArgb(218, 216, 141)) > 50 && ProcessorUtils.ColorDist(inv, Color.FromArgb(240, 240, 141)) > 50) {
                            bitmap.SetPixel(x, y, Color.White);
                            continue;
                        }

                        bitmap.SetPixel(x, y, Color.FromArgb(inv.ToArgb() ^ 0xffffff));
                    }
                }

                var ocrResult = speedOCR.Process(bitmap);
                rawSpeed = ocrResult.GetText().Trim().Replace(",", ".").Replace(" ", "").Replace("?", "").Replace("o", "0").Replace("g", "9");
                ocrResult.Dispose();
            }

            if (rawSpeed.Length <= 0 || rawSpeed.Contains("No")) {
                return null;
            }

            try {
                speed = double.Parse(rawSpeed);
                if (rawSpeed.StartsWith("0") && !rawSpeed.Contains(".")) {
                    speed /= 10.0;
                }
            } catch (FormatException) {
                return null;
            }

            return speed;
        }

        private (double, bool)? OCRDistanceAndIsKiloRead() {
            Rectangle distanceBounds = new Rectangle(365, 140, 115, 47);

            string rawDistance;
            bool kilo;
            double distance;
            var dotExists = false;

            using (Bitmap bitmap = ProcessorUtils.Screenshot(distanceBounds)) {
                for (var x = 0; x < bitmap.Width; x++) {
                    for (var y = 0; y < bitmap.Height; y++) {
                        Color inv = bitmap.GetPixel(x, y);

                        if (inv.R < 160 && inv.G < 160 && inv.B < 160) {
                            bitmap.SetPixel(x, y, Color.Gray);
                            continue;
                        }

                        if (y > x * 1.4 + 19) {
                            bitmap.SetPixel(x, y, Color.Gray);
                            continue;
                        }

                        bitmap.SetPixel(x, y, Color.FromArgb(inv.ToArgb() ^ 0xffffff));
                    }
                }

                // find floating point manually to divide by 10 correctly - reliable solution if made properly
                {
                    var dotY = distanceBounds.Height - 14;
                    var dCnt = 0;
                    for (var x = 5; x < bitmap.Width * 0.5; x++) {
                        var i = bitmap.GetPixel(x, dotY);
                        var i2 = bitmap.GetPixel(x - 1, dotY - 3); // 1
                        var i3 = bitmap.GetPixel(x, dotY - 8); // 7
                        if (ProcessorUtils.ColorDist(i, Color.Gray) > 150) {
                            //Console.WriteLine(x + " DOT " + ColorDist(i, Color.White));
                            dCnt += 1;
                        } else {
                            if (dCnt > 0)
                                //Console.WriteLine(x + " CHK");
                                if (dCnt > 2 && dCnt < 6 && ProcessorUtils.ColorDist(i2, Color.Black) > 200 && ProcessorUtils.ColorDist(i3, Color.Black) > 200) {
                                    //Console.WriteLine(x + "A");
                                    dotExists = true;
                                }
                            dCnt = 0;
                        }
                    }
                }

                var ocrResult = stationOCR.Process(bitmap);
                //if (ocrResult.GetMeanConfidence() < 0.1) {
                //    ocrResult.Dispose();
                //    return null;
                //}
                rawDistance = ocrResult.GetText().Trim().Replace(" ", "");
                ocrResult.Dispose();
            }

            if (rawDistance.Length <= 0 || rawDistance.Contains("Empty")) {
                return null;
            }

            kilo = rawDistance.EndsWith("km");
            try {
                distance = double.Parse(rawDistance.Substring(0, rawDistance.Length - (kilo ? 2 : 1)));

                if (dotExists && !rawDistance.Contains(".") && kilo) {
                    distance /= 10;
                }
            } catch (FormatException) {
                return null;
            }

            return (distance, kilo);
        }

        private int? OCRGradientRead() {
            Rectangle gradientLocator = new Rectangle(2381, 1100, 1, 60);

            int yFound = -1;

            // Find the gradient
            using (Bitmap bitmap = ProcessorUtils.Screenshot(gradientLocator)) {
                for (int i = bitmap.Height - 1; i >= 0; i--) {
                    if (ProcessorUtils.ColorDist(Color.FromArgb(120, 120, 120), bitmap.GetPixel(0, i)) < 50) {
                        yFound = i;
                        break;
                    }
                }
            }

            if (yFound == -1) return null;

            Rectangle gradientBounds = new Rectangle(2338, gradientLocator.Top + yFound + 3, 64, 40);

            string value = "";
            int num;

            using (Bitmap bitmap = ProcessorUtils.Screenshot(gradientBounds)) {
                bool checkSegment(int x) {
                    int s = 0;
                    for (int y = 12; y <= 18; y++) {
                        if (d(x, y) == 1) s++;
                    }
                    if (s < 5) s = 0;

                    for (int y = 22; y <= 29; y++) {
                        if (d(x, y) == 1) s++;
                    }

                    return s >= 6;
                }

                int d(int x, int y) {
                    var p = bitmap.GetPixel(x, y);
                    if (p.R > 170 && p.G > 170 && p.B > 170) {
                        return 1;
                    } else return 0;
                }

                int readSegmentedNumber(int topX, int topY) {
                    int s = 0;
                    s += d(topX + 7, topY + 1) << 6;
                    s += d(topX + 1, topY + 6) << 5;
                    s += d(topX + 12, topY + 6) << 4;
                    s += d(topX + 7, topY + 12) << 3;
                    s += d(topX + 1, topY + 17) << 2;
                    s += d(topX + 12, topY + 17) << 1;
                    s += d(topX + 7, topY + 23) << 0;

                    return s;
                }

                for (int x = bitmap.Width - 1; x >= 13; x--) {
                    if (d(x, 0) == 1) return null;
                    if (checkSegment(x)) {
                        int v = readSegmentedNumber(x - 13, 8);

                        //Console.WriteLine("SEGMENT " + x + " " + Convert.ToString(v, 2));

                        x -= 14;

                        switch (v) {
                            case 0b1110111: // 0
                                value = "0" + value;
                                break;
                            case 0b0010010: // 1
                                value = "1" + value;
                                break;
                            case 0b1011101: // 2
                                value = "2" + value;
                                break;
                            case 0b1011011: // 3
                                value = "3" + value;
                                break;
                            case 0b0111010: // 4
                                value = "4" + value;
                                break;
                            case 0b1101011: // 5
                                value = "5" + value;
                                break;
                            case 0b1101111: // 6
                                value = "6" + value;
                                break;
                            case 0b1110010: // 7
                                value = "7" + value;
                                break;
                            case 0b1111111: // 8
                                value = "8" + value;
                                break;
                            case 0b1111011: // 9
                                value = "9" + value;
                                break;
                            default:
                                break;
                        }

                        //Console.WriteLine(value);
                    }
                }
            }

            if (value.Length <= 0 || value.Contains("No")) {
                return null;
            }

            var downHill = false;
            // Downhill detection
            using (Bitmap bitmap = ProcessorUtils.Screenshot(new Rectangle(2316, gradientLocator.Top + yFound - 20, 1, 1))) {
                if (ProcessorUtils.ColorDist(bitmap.GetPixel(0, 0), Color.FromArgb(119, 119, 119)) < 12) {
                    downHill = true;
                }
            }

            if (downHill) value = "-" + value;

            try {
                num = int.Parse(value);
            } catch (FormatException) {
                return null;
            }

            return num;
        }

        #endregion
        #region Route plan OCR

        private (double, bool)? OCRSpdLimDistanceAndIsKiloRead(int yOffset) {
            Rectangle spdLimDistanceBounds = new Rectangle(2234, 286 + yOffset, 100, 34);

            string rawDistance;
            bool kilo;
            double distance;
            var dotExists = false;

            using (Bitmap bitmap = ProcessorUtils.Screenshot(spdLimDistanceBounds)) {
                for (var x = 0; x < bitmap.Width; x++) {
                    int colorTolerance = (x * -50 / bitmap.Width) + 240;
                    for (var y = 0; y < bitmap.Height; y++) {
                        Color inv = bitmap.GetPixel(x, y);

                        if (inv.R < colorTolerance && inv.G < colorTolerance && inv.B < colorTolerance) {
                            bitmap.SetPixel(x, y, Color.White);
                            continue;
                        }

                        bitmap.SetPixel(x, y, Color.FromArgb(inv.ToArgb() ^ 0xffffff));
                    }
                }

                {
                    var dotY = spdLimDistanceBounds.Height - 6;
                    var dCnt = 0;
                    for (var x = 0; x < bitmap.Width * 0.6; x++) {
                        var i = bitmap.GetPixel(x, dotY);
                        var i2 = bitmap.GetPixel(x, dotY - 3); // 1
                        var i3 = bitmap.GetPixel(x, dotY - 8); // 7
                        if (ProcessorUtils.ColorDist(i, Color.White) > 200) {
                            //Console.WriteLine(x + " DOT " + ColorDist(i, Color.White));
                            dCnt += 1;
                        } else {
                            if (dCnt > 0)
                                //Console.WriteLine(x + " CHK");
                                if (dCnt > 2 && dCnt < 6 && ProcessorUtils.ColorDist(i2, Color.Black) > 200 && ProcessorUtils.ColorDist(i3, Color.Black) > 200) {
                                    //Console.WriteLine(x + "A");
                                    dotExists = true;
                                }
                            dCnt = 0;
                        }
                    }
                }

                var ocrResult = planOCR.Process(bitmap);
                rawDistance = ocrResult.GetText().Trim().Replace(" ", "");
                ocrResult.Dispose();
            }

            if (rawDistance.Length <= 0 || rawDistance.Contains("Empty")) {
                return null;
            }

            kilo = rawDistance.EndsWith("km");
            try {
                distance = double.Parse(rawDistance.Substring(0, rawDistance.Length - (kilo ? 2 : 1)));

                if (dotExists && !rawDistance.Contains(".")) {
                    distance /= 10;
                }
            } catch (FormatException) {
                return null;
            }

            return (distance, kilo);
        }

        private double? OCRSpdLimSpeedRead(int yOffset = 0) {
            Rectangle speedLimBounds = new Rectangle(2388, 274 + yOffset, 79, 36);

            string rawSpeed;
            double speed;

            using (Bitmap bitmap = ProcessorUtils.Screenshot(speedLimBounds)) {
                ProcessorUtils.OptimiseForOCR(bitmap, 230);

                var ocrResult = planOCR.Process(bitmap);
                rawSpeed = ocrResult.GetText().Trim().Replace(" ", "");
                ocrResult.Dispose();
            }

            if (rawSpeed.Length <= 0 || rawSpeed.Contains("No")) {
                return null;
            }

            try {
                speed = double.Parse(rawSpeed);
            } catch (FormatException) {
                return null;
            }

            return speed;
        }

        private bool OCRSpdLimExists(int yOffset = 0) {
            Rectangle speedLimBounds = new Rectangle(2384, 309 + yOffset, 76, 21);

            string rawRes;

            using (Bitmap bitmap = ProcessorUtils.Screenshot(speedLimBounds)) {
                ProcessorUtils.OptimiseForOCR(bitmap, 160);

                var ocrResult = kmhOCR.Process(bitmap);
                rawRes = ocrResult.GetText().Trim().Replace(" ", "");
                ocrResult.Dispose();
                bitmap.Dispose();
            }

            //Console.WriteLine(rawRes);
            return rawRes.ToLower().Contains("km");
        }

        private readonly Color cG = Color.FromArgb(57, 187, 79);
        private readonly Color cY = Color.FromArgb(216, 226, 81);
        private readonly Color cR = Color.FromArgb(209, 33, 33);

        private int SSGetSignalState() {
            Rectangle bounds = new Rectangle(2407, 231, 24, 19);

            using (Bitmap bitmap = ProcessorUtils.Screenshot(bounds)) {
                var c = bitmap.GetPixel(3, 16);
                var dG = ProcessorUtils.ColorDist(c, cG);
                var dY = ProcessorUtils.ColorDist(c, cY);
                var dR = ProcessorUtils.ColorDist(c, cR);
                var isSignal = ProcessorUtils.ColorDist(Color.White, bitmap.GetPixel(19, 2)) > 160;

                Console.WriteLine(dG + ", " + dY + ", " + dR + ", " + ProcessorUtils.ColorDist(Color.White, bitmap.GetPixel(19, 2)));

                if (!isSignal) {
                    return -1;
                } else if (dG < 80) {
                    return 0;
                } else if (dY < 80) {
                    return 1;
                } else if (dR < 80) {
                    return 2;
                } else {
                    return -1;
                }
            }
        }

        private bool SSGetThrottleLockState() {
            Rectangle redBoxBounds = new Rectangle(2013, 1152, 10, 3);

            using (Bitmap bitmap = ProcessorUtils.Screenshot(redBoxBounds)) {
                if (ProcessorUtils.ColorDist(bitmap.GetPixel(2, 1), Color.Red) < 50) { // Can't move
                    return true;
                }
            }

            return false;
        }

        #endregion
        #region Game mission OCR while stopped
        public int SSGetStationState() {
            Rectangle doorIconBounds = new Rectangle(2125, 1119, 60, 5);
            Rectangle missionBounds = new Rectangle(475, 112, 303, 50);

            int value = 0;

            using (Bitmap bitmap = ProcessorUtils.Screenshot(doorIconBounds)) {
                if (ProcessorUtils.ColorDist(bitmap.GetPixel(2, 2), Color.White) < 20) { // Left doors open now
                    value += 1 << 0;
                }
                if (ProcessorUtils.ColorDist(bitmap.GetPixel(55, 2), Color.White) < 20) { // Right doors open now
                    value += 1 << 1;
                }
            }
            using (Bitmap bitmap = ProcessorUtils.Screenshot(missionBounds)) {
                ProcessorUtils.OptimiseForOCR(bitmap, 200);
                var ocrResult = gameMissionOCR.Process(bitmap);
                var text = ocrResult.GetText().ToLower();
                if (text.Contains("lock") && !text.Contains("unlock")) {
                    value += 1 << 2;
                }
                ocrResult.Dispose();
            }
            return value;
        }
        #endregion
    }
}
