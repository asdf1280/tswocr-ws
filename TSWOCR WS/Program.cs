using System;
using System.Drawing;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Tesseract;
using WebSocketSharp;
using WebSocketSharp.Net;
using WebSocketSharp.Server;

namespace TSWOCR_WS {
    class WebsocketProcessor : WebSocketBehavior {
        public static double speed = 0;
        public static double distance = 0;
        public static double sld = 0;
        public static double slv = 0;
        public static int sgs = 0;
        public static double sgd = 0;
        public static int stationState = -1;

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
                Send(speed + ";" + distance + ";" + sld + ";" + slv + ";" + sgs + ";" + sgd);
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
                    processor.ReadTrainSupervision(out double spdLimDist, out double spdLimVal, out int signal, out double signalDist);

                    WebsocketProcessor.slv = spdLimVal;
                    WebsocketProcessor.sld = spdLimDist;
                    WebsocketProcessor.sgs = signal;
                    WebsocketProcessor.sgd = signalDist;

                    Console.WriteLine("SUP " + spdLimVal + " in " + spdLimDist + " / " + signal + "s in " + signalDist);

                    Thread.Sleep(delay);
                }
            }) {
                IsBackground = false
            }.Start();

            // Train Position Reading
            while (true) {
                processor.ReadTrainState(out double speed, out double distance);

                var mpsSpeed = speed / 3.6;

                //if (speed < 1) continue;

                double meters = distance;

                Console.WriteLine("POS " + speed + " -> " + Math.Round(distance));

                if (speed <= 0) {
                    int ss = processor.SSGetStationState();
                    WebsocketProcessor.stationState = ss;
                } else {
                    WebsocketProcessor.stationState = -1;
                }

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

    class RawDataProcessor {
        private TesseractEngine stationOCR;
        private TesseractEngine speedOCR;
        private TesseractEngine planOCR;
        private TesseractEngine kmhOCR;
        private TesseractEngine gameMissionOCR;
        public RawDataProcessor() {
            stationOCR = Program.CreateEngine("0123456789.km", "deu", PageSegMode.SingleLine);
            speedOCR = Program.CreateEngine("0123456789.", "deu", PageSegMode.SparseText);
            planOCR = Program.CreateEngine("0123456789.km", "deu", PageSegMode.SingleLine);
            kmhOCR = Program.CreateEngine("KMHkm/h", "deu", PageSegMode.SingleLine);
            gameMissionOCR = Program.CreateEngine("", "eng", PageSegMode.SingleLine);
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

        public void ReadTrainState(out double speed, out double distance) {
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

            // Data output

            lastDataCall = currentTime;
            prevSpeed = speed = finalSpeed;
            prevDetectedDistance = memoryDistance;
            distance = finalDistance;
            anotherDist = distanceMeters ?? 0;
        }

        public void ReadTrainSupervision(out double spdLimDist, out double spdLimValue, out int signal, out double signalDist) {
            // Signal data detection
            var signalState = SSGetSignalState();
            signal = signalState;
            if (signalState <= 0) {
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

            using (Bitmap bitmap = Screenshot(speedBounds)) {
                OptimiseForOCR(bitmap, 230);
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

            using (Bitmap bitmap = Screenshot(distanceBounds)) {
                for (var x = 0; x < bitmap.Width; x++) {
                    for (var y = 0; y < bitmap.Height; y++) {
                        Color inv = bitmap.GetPixel(x, y);

                        if (inv.R < 200 && inv.G < 200 && inv.B < 200) {
                            bitmap.SetPixel(x, y, Color.White);
                            continue;
                        }

                        if (y > x * 1.4 + 19) {
                            bitmap.SetPixel(x, y, Color.White);
                            continue;
                        }

                        bitmap.SetPixel(x, y, Color.FromArgb(inv.ToArgb() ^ 0xffffff));
                    }
                }
                var ocrResult = stationOCR.Process(bitmap);
                rawDistance = ocrResult.GetText().Trim().Replace(" ", "");
                ocrResult.Dispose();
            }

            if (rawDistance.Length <= 0 || rawDistance.Contains("Empty")) {
                return null;
            }

            kilo = rawDistance.EndsWith("km");
            try {
                distance = double.Parse(rawDistance.Substring(0, rawDistance.Length - (kilo ? 2 : 1)));
            } catch (FormatException) {
                return null;
            }

            return (distance, kilo);
        }

        #endregion
        #region Route plan OCR

        private (double, bool)? OCRSpdLimDistanceAndIsKiloRead(int yOffset) {
            Rectangle spdLimDistanceBounds = new Rectangle(2234, 286 + yOffset, 100, 34);

            string rawDistance;
            bool kilo;
            double distance;
            var dotExists = false;

            using (Bitmap bitmap = Screenshot(spdLimDistanceBounds)) {
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
                        if (ColorDist(i, Color.White) > 200) {
                            //Console.WriteLine(x + " DOT " + ColorDist(i, Color.White));
                            dCnt += 1;
                        } else {
                            if (dCnt > 0)
                                //Console.WriteLine(x + " CHK");
                                if (dCnt > 2 && dCnt < 6 && ColorDist(i2, Color.Black) > 200 && ColorDist(i3, Color.Black) > 200) {
                                    //Console.WriteLine(x + "A");
                                    dotExists = true;
                                }
                            dCnt = 0;
                        }
                    }
                }

                //bitmap.Save("test.png", System.Drawing.Imaging.ImageFormat.Png);
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

            using (Bitmap bitmap = Screenshot(speedLimBounds)) {
                OptimiseForOCR(bitmap, 230);

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

            using (Bitmap bitmap = Screenshot(speedLimBounds)) {
                OptimiseForOCR(bitmap, 160);

                //bitmap.Save("test.png");
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

            using (Bitmap bitmap = Screenshot(bounds)) {
                var c = bitmap.GetPixel(3, 16);
                var dG = ColorDist(c, cG);
                var dY = ColorDist(c, cY);
                var dR = ColorDist(c, cR);
                var isSignal = ColorDist(Color.White, bitmap.GetPixel(19, 2)) > 160;

                Console.WriteLine(dG + ", " + dY + ", " + dR + ", " + ColorDist(Color.White, bitmap.GetPixel(19, 2)));

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

        #endregion
        #region Game mission OCR while stopped
        public int SSGetStationState() {
            Rectangle doorIconBounds = new Rectangle(2125, 1119, 60, 5);
            Rectangle missionBounds = new Rectangle(475, 112, 303, 50);

            int value = 0;

            using (Bitmap bitmap = Screenshot(doorIconBounds)) {
                if (ColorDist(bitmap.GetPixel(2, 2), Color.White) < 20) { // Left doors open now
                    value += 1 << 0;
                }
                if (ColorDist(bitmap.GetPixel(55, 2), Color.White) < 20) { // Right doors open now
                    value += 1 << 1;
                }
            }
            using (Bitmap bitmap = Screenshot(missionBounds)) {
                OptimiseForOCR(bitmap, 200);
                var ocrResult = gameMissionOCR.Process(bitmap);
                var text = ocrResult.GetText().ToLower();
                if (text.Contains("lock doors") && !text.Contains("unlock doors")) {
                    value += 1 << 2;
                }
                ocrResult.Dispose();
            }
            return value;
        }
        #endregion

        static Bitmap Screenshot(Rectangle bounds) {
            var bm = new Bitmap(bounds.Width, bounds.Height);
            using (Graphics g = Graphics.FromImage(bm)) {
                g.CopyFromScreen(new Point(bounds.Left, bounds.Top), Point.Empty, bounds.Size);
            }
            return bm;
        }

        static void OptimiseForOCR(Bitmap image, int maxColor) {
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

        static double ColorDist(Color a, Color b) {
            return Math.Sqrt(Math.Pow(a.R - b.R, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.G - b.G, 2));
        }
    }
}
