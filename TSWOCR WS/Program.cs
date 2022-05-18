using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Drawing;
using System.IO;
using System.Linq;
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
        
        public static event EventHandler ResetRequested;
        
        public WebsocketProcessor() {

        }

        protected override void OnMessage(MessageEventArgs e) {
            if (e.Data == "reset")
            {
                ResetRequested(null, null);
            }
            else
                //new Thread(() =>
                //{
                //    var end = DateTime.Now.Ticks + (2000 * TimeSpan.TicksPerMillisecond);
                //    while(DateTime.Now.Ticks < end && this.State == WebSocketState.Open)
                //    {
                //        Send(speed + ";" + distance);
                //        Thread.Sleep(50);
                //    }
                //}).Start();
                Send(speed + ";" + distance);
        }
    }
    class Program {
        static long lastTick;
        static void Main(string[] args) {
            TesseractEngine ocr = new TesseractEngine("tessdata", "eng");
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

                processor.ReadTrainState(out double speed, out double distance);

                var mpsSpeed = speed / 3.6;

                //if (speed < 1) continue;

                double meters = distance;

                Console.WriteLine(speed + " -> " + Math.Round(distance));

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
            Rectangle speedBounds = new Rectangle(2070, 1235, 120, 40);

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
                var newBitmap = new Bitmap(speedBounds.Width * 10, speedBounds.Height * 10);
                var gr = Graphics.FromImage(newBitmap);
                gr.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                gr.DrawImage(bitmap, 0, 0, bitmap.Width * 5, bitmap.Height * 5);
                //newBitmap.Save("test.jpg", System.Drawing.Imaging.ImageFormat.Jpeg);
                var ocrResult = ocr.Process(bitmap);
                rawSpeed = ocrResult.GetText().Trim().Replace(" ", "").Replace("?", "").Replace("o", "0");
                ocrResult.Dispose();
                gr.Dispose();
                bitmap.Dispose();
                newBitmap.Dispose();
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
                var newBitmap = new Bitmap(distanceBounds.Width * 10, distanceBounds.Height * 10);
                var gr = Graphics.FromImage(newBitmap);
                gr.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                gr.DrawImage(bitmap, 0, 0, bitmap.Width * 5, bitmap.Height * 5);
                //newBitmap.Save("test.jpg", System.Drawing.Imaging.ImageFormat.Jpeg);
                var ocrResult = ocr.Process(bitmap);
                rawDistance = ocrResult.GetText().Trim().Replace(" ", "").Replace(",", ".").Replace("o", "0");
                ocrResult.Dispose();
                gr.Dispose();
                bitmap.Dispose();
                newBitmap.Dispose();
            }

            if (rawDistance.Length <= 0 || rawDistance.Contains("Empty")) {
                return null;
            }

            kilo = rawDistance.EndsWith("km");
            try {
                distance = Double.Parse(rawDistance.Substring(0, rawDistance.Length - (kilo ? 2 : 1)));
            } catch (FormatException) {
                return null;
            }

            return (distance, kilo);
        }

        static double ColorDist(Color a, Color b) {
            return Math.Sqrt(Math.Pow(a.R - b.R, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.G - b.G, 2));
        }
    }
}
