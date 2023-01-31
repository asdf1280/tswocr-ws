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

        private static TcpClient cl = null;

        public static event EventHandler ResetRequested;

        public WebsocketProcessor() {

        }

        protected override void OnMessage(MessageEventArgs e) {
            if (e.Data == "reset") {
                ResetRequested(null, null);
            } else if (Regex.IsMatch(e.Data, @"ap(-1|[\d]+)")) {
                var m = Regex.Match(e.Data, @"ap(-1|[\d]+)");
                var brake = int.Parse(m.Groups[1].Value);
                new Thread(() => {
                    try {
                        if (cl == null && brake == -1) return;
                        if (cl == null || !cl.Connected) {
                            cl = new TcpClient("127.0.0.1", 4776);
                        }
                        BinaryWriter bw = new BinaryWriter(cl.GetStream());
                        bw.Write(brake);
                        bw.Flush();

                        if (brake == -1) {
                            bw.Write(-2);
                            bw.Flush();
                            cl.Close();
                            cl = null;
                        } else {
                            bw.Dispose();
                        }
                    } catch {

                    }
                    //catch {

                    //}
                }).Start();
            } else
                //new Thread(() =>
                //{
                //    var end = DateTime.Now.Ticks + (2000 * TimeSpan.TicksPerMillisecond);
                //    while(DateTime.Now.Ticks < end && this.State == WebSocketState.Open)
                //    {
                //        Send(speed + ";" + distance);
                //        Thread.Sleep(50);
                //    }
                //}).Start();
                Send(speed + ";" + distance + ";" + sld + ";" + slv + ";" + sgs + ";" + sgd);
        }
    }
    class Program {
        static long lastTick;
        static void Main(string[] args) {
            TesseractEngine ocr = new TesseractEngine("tessdata", "eng");
            ocr.SetVariable("tessedit_char_whitelist", "0123456789g.,km KMH");
            ocr.DefaultPageSegMode = PageSegMode.SingleLine;
            ocr.SetVariable("debug_file", "NUL");
            RawDataProcessor processor = new RawDataProcessor(ocr);

            new Thread(() => {
                while (true) {
                    Console.ReadLine();
                    Console.WriteLine("Reset the speed and distance");
                    processor.Reset();
                }
            }) {
                IsBackground = true
            }.Start();

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
            while (true) {
                var prevTick = lastTick;
                lastTick = DateTime.Now.Ticks;

                processor.ReadTrainState(out double speed, out double distance, out double spdLimDist, out double spdLimVal, out int signal, out double signalDist);

                var mpsSpeed = speed / 3.6;

                //if (speed < 1) continue;

                double meters = distance;

                Console.WriteLine(speed + " -> " + Math.Round(distance) + " / " + spdLimVal + " in " + spdLimDist + " / " + signal + "s in " + signalDist);

                WebsocketProcessor.slv = spdLimVal;
                WebsocketProcessor.sld = spdLimDist;
                WebsocketProcessor.sgs = signal;
                WebsocketProcessor.sgd = signalDist;

                if (speed == 0 && distance == 0) { // stopped
                    Thread.Sleep(1000);
                    WebsocketProcessor.speed = 0;
                    WebsocketProcessor.distance = -1;
                }

                if (/*speed < 10 || */distance == 0) {
                    WebsocketProcessor.speed = 0;
                    WebsocketProcessor.distance = -1;
                    continue;
                }

                Thread.Sleep(50);
                WebsocketProcessor.speed = speed;
                WebsocketProcessor.distance = distance;
            }
        }

    }

    class RawDataProcessor {
        private TesseractEngine ocr;
        public RawDataProcessor(TesseractEngine engine) {
            this.ocr = engine;
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

        public void ReadTrainState(out double speed, out double distance, out double spdLimDist, out double spdLimValue, out int signal, out double signalDist) {
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
            } else if (distanceDiv10Valid && isKilo && distanceMeters / 10.0 >= 1000) {
                memoryDistance = distanceMeters / 10.0 ?? 0;
                anotherValidCount = 0;
                properDistanceFound = true;
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

            // Speed limit detection
            var speedLimitDistanceData = OCRSpdLimDistanceAndIsKiloRead(0);
            var speedLimitValueData = OCRSpdLimSpeedRead();
            var speedLimitExistsData = OCRSpdLimExists();

            if (!speedLimitExistsData) {
                spdLimDist = -1;
                spdLimValue = -1;
            } else {
                if (speedLimitDistanceData != null) {
                    var dd = speedLimitDistanceData.Value.Item1 * (speedLimitDistanceData.Value.Item2 ? 1000 : 1);
                    //if (dd > 1000)
                    spdLimDist = dd;
                    //else
                    //    spdLimDist = -1;
                } else {
                    spdLimDist = -1;
                }
                if (speedLimitValueData != null) {
                    spdLimValue = speedLimitValueData.Value;
                } else {
                    spdLimValue = -1;
                }
            }

            // Signal data detection
            var signalState = OCRGetSignalState();
            signal = signalState;
            if (signalState <= 0) {
                signalDist = -1;
            } else {
                var signalDistanceData = OCRSpdLimDistanceAndIsKiloRead(-67);
                if (signalDistanceData != null) {
                    var dd = signalDistanceData.Value.Item1 * (signalDistanceData.Value.Item2 ? 1000 : 1);
                    //if (dd > 1000)
                    signalDist = dd;
                    //else
                    //    signalDist = -1;
                } else {
                    signalDist = -1;
                }
            }

            // Data output

            lastDataCall = currentTime;
            prevSpeed = speed = finalSpeed;
            prevDetectedDistance = memoryDistance;
            distance = finalDistance;
            anotherDist = distanceMeters ?? 0;
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

        private double? OCRSpeedRead() {
            //Rectangle speedBounds = new Rectangle(2070, 1235, 120, 40);
            Rectangle speedBounds = new Rectangle(2021, 1210, 131, 49);

            string rawSpeed;
            double speed;

            using (Bitmap bitmap = new Bitmap(speedBounds.Width, speedBounds.Height)) {
                using (Graphics g = Graphics.FromImage(bitmap)) {
                    g.CopyFromScreen(new Point(speedBounds.Left, speedBounds.Top), Point.Empty, speedBounds.Size);
                }
                for (var y = 0; y < bitmap.Height; y++) {
                    for (var x = 0; x < bitmap.Width; x++) {
                        //if (ColorDist(bitmap.GetPixel(x, y), Color.White) < 50)
                        //{
                        //    bitmap.SetPixel(x, y, Color.Black);
                        //}
                        //else
                        //{
                        //    bitmap.SetPixel(x, y, Color.White);

                        //}
                        Color inv = bitmap.GetPixel(x, y);

                        if (inv.R < 150 && inv.G < 150 && inv.B < 150) {
                            bitmap.SetPixel(x, y, Color.White);
                            continue;
                        }

                        inv = Color.FromArgb(255, (255 - inv.R), (255 - inv.G), (255 - inv.B));
                        bitmap.SetPixel(x, y, inv);
                    }
                }
                var ocrResult = ocr.Process(bitmap);
                rawSpeed = ocrResult.GetText().Trim().Replace(",", ".").Replace(" ", "").Replace("?", "").Replace("o", "0").Replace("g", "9");
                ocrResult.Dispose();
                bitmap.Dispose();
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
            Rectangle distanceBounds = new Rectangle(370, 143, 90, 39);

            string rawDistance;
            bool kilo;
            double distance;

            using (Bitmap bitmap = new Bitmap(distanceBounds.Width, distanceBounds.Height)) {
                using (Graphics g = Graphics.FromImage(bitmap)) {
                    g.CopyFromScreen(new Point(distanceBounds.Left, distanceBounds.Top), Point.Empty, distanceBounds.Size);
                }
                for (var y = 0; y < bitmap.Height; y++) {
                    for (var x = 0; x < bitmap.Width; x++) {
                        //if (ColorDist(bitmap.GetPixel(x, y), Color.White) < 50) {
                        //    bitmap.SetPixel(x, y, Color.Black);
                        //} else {
                        //    bitmap.SetPixel(x, y, Color.White);

                        //}
                        Color inv = bitmap.GetPixel(x, y);

                        if (inv.R < 150 && inv.G < 150 && inv.B < 150) {
                            bitmap.SetPixel(x, y, Color.White);
                            continue;
                        }

                        inv = Color.FromArgb(255, (255 - inv.R), (255 - inv.G), (255 - inv.B));
                        bitmap.SetPixel(x, y, inv);
                    }
                }
                //newBitmap.Save("test.jpg", System.Drawing.Imaging.ImageFormat.Jpeg);
                var ocrResult = ocr.Process(bitmap);
                rawDistance = ocrResult.GetText().Trim().Replace(" ", "").Replace(",", ".").Replace("o", "0").Replace("g", "9");
                ocrResult.Dispose();
                bitmap.Dispose();
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


        private (double, bool)? OCRSpdLimDistanceAndIsKiloRead(int yOffset) {
            Rectangle spdLimDistanceBounds = new Rectangle(2235, 286 + yOffset, 105, 34);

            string rawDistance;
            bool kilo;
            double distance;
            var dotExists = false;

            using (Bitmap bitmap = new Bitmap(spdLimDistanceBounds.Width, spdLimDistanceBounds.Height)) {
                using (Graphics g = Graphics.FromImage(bitmap)) {
                    g.CopyFromScreen(new Point(spdLimDistanceBounds.Left, spdLimDistanceBounds.Top), Point.Empty, spdLimDistanceBounds.Size);
                }
                for (var y = 0; y < bitmap.Height; y++) {
                    for (var x = 0; x < bitmap.Width; x++) {
                        //if (ColorDist(bitmap.GetPixel(x, y), Color.White) < 50) {
                        //    bitmap.SetPixel(x, y, Color.Black);
                        //} else {
                        //    bitmap.SetPixel(x, y, Color.White);
                        //}
                        Color inv = bitmap.GetPixel(x, y);

                        if (inv.R < 150 && inv.G < 150 && inv.B < 150) {
                            bitmap.SetPixel(x, y, Color.White);
                            continue;
                        }

                        inv = Color.FromArgb(255, (255 - inv.R), (255 - inv.G), (255 - inv.B));
                        bitmap.SetPixel(x, y, inv);
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

                //var newBitmap = new Bitmap(spdLimDistanceBounds.Width * 5, spdLimDistanceBounds.Height * 5);
                //var gr = Graphics.FromImage(newBitmap);
                //gr.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                //gr.DrawImage(bitmap, 0, 0, newBitmap.Width, newBitmap.Height);
                //newBitmap.Save("test.jpg", System.Drawing.Imaging.ImageFormat.Jpeg);
                var ocrResult = ocr.Process(bitmap);
                rawDistance = ocrResult.GetText().Trim().Replace(" ", "").Replace(",", ".").Replace("o", "0").Replace("g", "9");
                ocrResult.Dispose();
                bitmap.Dispose();
                //newBitmap.Dispose();

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

        private double? OCRSpdLimSpeedRead() {
            Rectangle speedLimBounds = new Rectangle(2388, 274, 79, 36);

            string rawSpeed;
            double speed;

            using (Bitmap bitmap = new Bitmap(speedLimBounds.Width, speedLimBounds.Height)) {
                using (Graphics g = Graphics.FromImage(bitmap)) {
                    g.CopyFromScreen(new Point(speedLimBounds.Left, speedLimBounds.Top), Point.Empty, speedLimBounds.Size);
                }
                for (var y = 0; y < bitmap.Height; y++) {
                    for (var x = 0; x < bitmap.Width; x++) {
                        Color inv = bitmap.GetPixel(x, y);

                        if (inv.R < 150 && inv.G < 150 && inv.B < 150) {
                            bitmap.SetPixel(x, y, Color.White);
                            continue;
                        }

                        inv = Color.FromArgb(255, (255 - inv.R), (255 - inv.G), (255 - inv.B));
                        bitmap.SetPixel(x, y, inv);
                    }
                }

                //bitmap.Save("test.jpg", System.Drawing.Imaging.ImageFormat.Jpeg);
                var ocrResult = ocr.Process(bitmap);
                rawSpeed = ocrResult.GetText().Trim().Replace(" ", "").Replace("o", "0").Replace("g", "9");
                ocrResult.Dispose();
                bitmap.Dispose();
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

        private bool OCRSpdLimExists() {
            Rectangle speedLimBounds = new Rectangle(2384, 309, 76, 21);

            string rawRes;

            using (Bitmap bitmap = new Bitmap(speedLimBounds.Width, speedLimBounds.Height)) {
                using (Graphics g = Graphics.FromImage(bitmap)) {
                    g.CopyFromScreen(new Point(speedLimBounds.Left, speedLimBounds.Top), Point.Empty, speedLimBounds.Size);
                }
                for (var y = 0; y < bitmap.Height; y++) {
                    for (var x = 0; x < bitmap.Width; x++) {
                        Color inv = bitmap.GetPixel(x, y);

                        if (inv.R < 150 && inv.G < 150 && inv.B < 150) {
                            bitmap.SetPixel(x, y, Color.White);
                            continue;
                        }

                        inv = Color.FromArgb(255, (255 - inv.R), (255 - inv.G), (255 - inv.B));
                        bitmap.SetPixel(x, y, inv);
                    }
                }

                //bitmap.Save("test.jpg", System.Drawing.Imaging.ImageFormat.Jpeg);
                var ocrResult = ocr.Process(bitmap);
                rawRes = ocrResult.GetText().Trim().Replace(" ", "").Replace("o", "0").Replace("g", "9");
                ocrResult.Dispose();
                bitmap.Dispose();
            }

            //Console.WriteLine(rawRes);
            return rawRes.ToLower().Contains("km");
        }

        private readonly Color cG = Color.FromArgb(57, 187, 79);
        private readonly Color cY = Color.FromArgb(216, 226, 81);
        private readonly Color cR = Color.FromArgb(209, 33, 33);

        private int OCRGetSignalState() {
            Rectangle bounds = new Rectangle(2397, 209, 57, 53);

            using (Bitmap bitmap = new Bitmap(bounds.Width, bounds.Height)) {
                using (Graphics g = Graphics.FromImage(bitmap)) {
                    g.CopyFromScreen(new Point(bounds.Left, bounds.Top), Point.Empty, bounds.Size);
                }

                bitmap.Save("test.jpg");

                var c = bitmap.GetPixel(28, 44);
                var dG = ColorDist(c, cG);
                var dY = ColorDist(c, cY);
                var dR = ColorDist(c, cR);
                var isSignal = ColorDist(Color.White, bitmap.GetPixel(30, 25)) > 250;

                Console.WriteLine(dG + ", " + dY + ", " + dR + ", " + ColorDist(Color.White, bitmap.GetPixel(30, 25)));

                if (!isSignal) {
                    return -1;
                } else if (dG < 150) {
                    return 0;
                } else if (dY < 150) {
                    return 1;
                } else if (dR < 150) {
                    return 2;
                } else {
                    return -1;
                }
            }
        }

        static double ColorDist(Color a, Color b) {
            return Math.Sqrt(Math.Pow(a.R - b.R, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.G - b.G, 2));
        }
    }
}
