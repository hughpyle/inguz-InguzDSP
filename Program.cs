using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Xml;
using System.Threading;
using System.Reflection;
using System.Globalization;

using DSPUtil;

// Copyright (c) 2006-2009 by Hugh Pyle, inguzaudio.com

namespace InguzDSP
{
    class Program
    {
        #region Member vars

        // All static (may be set by the static filesystem-watcher)
        // Defaults are suitable for usual SlimServer configurations

        static char slash = Path.DirectorySeparatorChar;

        static bool _debug = false;

        static string _userID;   // The squeezebox client->id().  Not valid unless we have this specified.

        static string _pluginFolder;    // location of the executable
        static string _dataFolder;      // <appdata>\InguzEQ\            used for log trace
        static string _settingsFolder;  // <appdata>\InguzEQ\Settings\   used for config file
        static string _tempFolder;      // <appdata>\InguzEQ\Temp        used for EQ filter cache

        static string _configFile;      // <userID>.settings.conf in _settingsFolder

        static string _siggen = null;   // Specifies the signal-generator mode, if set (null otherwise)
        static ChannelFlag _siggenUseEQ = ChannelFlag.NONE;   // Do we process signal-generator tones via the EQ stack
        static int _sigparam1 = 0;      // Up to three parameters, meaning varies according to mode
        static int _sigparam2 = 0;
        static int _sigparam3 = 0;
        static string _sigparamA = "";
        static string _sigparamB = "";

        static string _impulsePath = null;
        static string _inPath = null;
        static string _outPath = null;
        static Stream _inStream = null; // for debugging only

        static bool _tail = true;
        static bool _slow = false;

        // weighted volume of the room correction filter (each channel)
        static List<double> _impulseVolumes = new List<double>();

        // input format
        static bool _bigEndian = false;         // default is little-endian input (intel)
        static uint _inputSampleRate = 0;
        static bool _isRawIn = true;
        static WaveFormat _rawtype = WaveFormat.PCM;
        static ushort _rawbits = 16;    // input bits per sample
        static ushort _rawchan = 2;     // input number of channels
        static bool _isBFormat = false;
        static bool _inputSPDIF = false;
        static bool _isBypass = false;

        static TimeSpan _startTime = TimeSpan.Zero;     // skip (part) of the input file (not applied to streams)

        // output format
        static double _gain = 0;
        static DitherType _dither = DitherType.SHAPED;

        static int _partitions = 4;
        static int _eqBands;
        static FilterProfile _eqValues = new FilterProfile();
        static double _eqLoudness;  // 0 to 100, 0 means NOP
        static double _eqFlatness;  // 0 to 100, 100 means NOP
        static string _matrixFilter = null;     // path of the 2-channel "matrix" filter (WAV)
        static string _bformatFilter = null;    // path of the ambisonic filter (AMB)
        static double _width;
        static int _depth;
        static double _balance;
        static int _skew;

        // SOX (or equivalent), used for resampling impulses
        static string _soxExe = "sox";
        static string _soxFmt = "\"{0}\" -r {1} \"{2}\" polyphase";

        // AFTEN (or equivalent), used for encoding multi-channel WAV to WAV-wrapped AC3
        static string _aftenExe = "aften";
        static string _aftenFmt = "-v 0 -b 640 -readtoeof 1 - -";
        static bool _aftenNeeded = false;

        // Ambisonic ("UHJ", "Blumlein", "Crossed" or "Matrix")
        static string _ambiType = null;

        // For crossed-mics stereo downmix, proportion of W to mix (1=cardioid, 0=figure8)
        static double _ambiCardioid = 0.7;

        // For crossed-mics stereo downmix, angle subtended by the mics (degrees; default 90)
        static double _ambiMicAngle = 90;

        // Soundfield rotation (degrees)
        static double _ambiRotateX = 0;
        static double _ambiRotateY = 0;
        static double _ambiRotateZ = 0;

        // Ambisonic (future - full decodes)
        static string _ambiMatrixFile = null;       // path of the 4-channel matrix file (xml)

        // Parameters
        static double _ambiDistance = 2.5;
        static double _ambiShelfFreq = 400;

        // Output format
        static bool _isRawOut = true;
        static ushort _outBits = 0;     // output bits per sample; can override; zero means same as source
        static uint _outRate = 0;
        static ushort _outChannels = 2;
        static WaveFormat _outFormat = WaveFormat.PCM;

        static IConvolver _MatrixConvolver;
        static IConvolver _MainConvolver;
        static IConvolver _EQConvolver;
        static Shuffler _widthShuffler;
        static Skewer _depthSkewer;
        static Skewer _skewSkewer;
        static WaveWriter _writer;
        static int _maxImpulseLength = 65536;

        static XmlDocument _configDocument = new XmlDocument();

        static Object _lockReadConfig = new Object();

        #endregion

        static TextWriter stdout
        {
            get
            {
                return System.Console.Out;
            }
        }

        static void DisplayUsage(string badArg)
        {
            if (badArg != null)
            {
                stdout.WriteLine("Parameter {0} was not recognized.", badArg);
            }
            stdout.WriteLine("Usage: InguzDSP -id clientID [-d outputbitdepth] [-r samplerate] [-wav] [-be]");
        }

        #region Main: check args & run
        // FileSystemWatcher requires FullTrust
        [System.Security.Permissions.PermissionSet(System.Security.Permissions.SecurityAction.Demand, Name = "FullTrust")]
        static void Main(string[] args)
        {
            try
            {
                // Find where this executable is launched from
                string[] cargs = Environment.GetCommandLineArgs();
                _pluginFolder = Path.GetDirectoryName(cargs[0]);
                // Trace.WriteLine("Seems to be running {0} in {1}", cargs[0], _pluginFolder);
                // _pluginFolder = Path.GetFullPath(Path.Combine(pathName, ".." + slash + ".." + slash + "Plugins" + slash + "InguzEQ" + slash));

                string appData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                _dataFolder = Path.GetFullPath(Path.Combine(appData, "InguzEQ" + slash));
                _settingsFolder = Path.GetFullPath(Path.Combine(appData, "InguzEQ" + slash + "Settings" + slash));
                _tempFolder = Path.GetFullPath(Path.Combine(appData, "InguzEQ" + slash + "Temp" + slash));
                Trace.FilePath = Path.Combine(_dataFolder, "log.txt");

                Trace.WriteLine("InguzDSP ({0}) {1}", DSPUtil.DSPUtil.GetVersionInfo(), String.Join(" ", args));
                bool ok = LoadConfig1();
                ushort n;
                for (int j = 0; ok && j < args.Length; j++)
                {
                    string arg = args[j];
                    switch (args[j].ToUpperInvariant())
                    {
                        case "-ID":
                            try
                            {
                                _userID = args[++j];
                            }
                            catch (Exception) { /* ignore if there is no arg */ }
                            break;

                        case "-WAV":
                            // the input has a WAV header, not raw
                            _isRawIn = false;
                            break;

                        case "-AMB":
                            // The input is Ambisonics B-Format (4-channel); decode to UHJ (etc)
                            // the input has a WAV header, not raw
                            _isBFormat = true;
                            _isRawIn = false;
                            break;

                        case "-WAVO":
                            // the output should have a WAV header, not raw
                            _isRawOut = false;
                            break;

                        case "-BE":
                            // the input is big-endian
                            _bigEndian = true;
                            break;

                        case "-INPUT":
                            try
                            {
                                _inPath = args[++j];
                            }
                            catch (Exception) { /* ignore if there is no arg */ }
                            break;

                        case "-OUTPUT":
                            try
                            {
                                _outPath = args[++j];
                            }
                            catch (Exception) { /* ignore if there is no arg */ }
                            break;

                        case "-D":
                            try
                            {
                                n = ushort.Parse(args[++j], CultureInfo.InvariantCulture);
                                if (n > 0) _outBits = n;
                            }
                            catch (Exception) { /* ignore if there is no arg */ }
                            break;

                        case "-R":
                            try
                            {
                                uint m = uint.Parse(args[++j], CultureInfo.InvariantCulture);
                                if (m > 0) _inputSampleRate = m;
                            }
                            catch (Exception) { /* ignore if there is no arg */ }
                            break;

                        case "-SKIP":
                            // SqueezeCenter always uses min:sec.fraction (even for >60 minutes)
                            try
                            {
                                string ts = args[++j];
                                string[] tsa = ts.Split(':', '.');
                                if (tsa.Length == 3)
                                {
                                    double tix = (double)(int.Parse(tsa[0]) * 60);
                                    tix += (double)(int.Parse(tsa[1]));
                                    tix += double.Parse("0." + tsa[2], CultureInfo.InvariantCulture);
                                    _startTime = new TimeSpan((long)(tix * 10000000));
                                }
                                else
                                {
                                    // As a fallback, try the built-in TimeSpan.Parse
                                    // which is dumb and inflexible (and it's always the InvariantCulture version, tsk)
                                    _startTime = TimeSpan.Parse(args[++j]);
                                }
                            }
                            catch (Exception) { /* ignore if there is no arg */ }
                            break;

                        case "-DEBUG":
                            Trace.UseConsole = true;
                            _debug = true;
                            break;

                        case "-":
                            // ignore
                            break;

                        case "<":
                            // only for debugger; shell takes care of this normally
                            string infile = args[++j];
                            _inStream = File.OpenRead(infile);
                            break;

                        default:
                            Trace.WriteLine("Unrecognized parameter: {0}", arg);
                            DisplayUsage(arg);
                            ok = false;
                            break;
                    }
                }
                if (ok && _userID == null)
                {
                    Trace.WriteLine("-id must be specified.");
                    DisplayUsage(null);
                    ok = false;
                }
                Trace.Prefix = _userID + " ";

                if (ok)
                {
                    InguzDSP(ok);
                }
            }
            catch (Exception e)
            {
                Show("InguzDSP: error", e.Message, 2);
                Trace.WriteLine(e.Message + " (" + e.GetHashCode() + ")" );
                Trace.WriteLine(e.StackTrace);
            }
            finally
            {
                stdout.Flush();
            }
        }
        #endregion


        static void InguzDSP(bool doRun)
        {
            DateTime dtStartRun = DateTime.Now;
            string sigdesc = null;

            // We need a few convolvers, of course
            if (_slow)
            {
                _MatrixConvolver = new SlowConvolver();
                _MainConvolver = new SlowConvolver();
                _EQConvolver = new SlowConvolver();
            }
            else
            {
                _MatrixConvolver = new FastConvolver();
                _MainConvolver = new FastConvolver();
                _EQConvolver = new FastConvolver();
            }

            // Shuffler
            _widthShuffler = new Shuffler();

            // Two skewers
            _depthSkewer = new Skewer(true);
            _skewSkewer = new Skewer(true);

            // Writer
            if (_outPath == null)
            {
                _writer = new WaveWriter();  // stdout
            }
            else
            {
                _writer = new WaveWriter(_outPath);
            }
            _writer.NumChannels = _outChannels;
            if (_debug)
            {
                TimeSpan ts = DateTime.Now.Subtract(dtStartRun);
                Trace.WriteLine("Setup " + ts.TotalMilliseconds);
            }

            /*
            DateTime exp = DSPUtil.DSPUtil.EXPIRY;
            if (exp != null)
            {
                if (DateTime.Now.CompareTo(exp) >= 0)
                {
                    Trace.WriteLine("**** THIS EVALUATION VERSION EXPIRED {0}", DSPUtil.DSPUtil.EXPIRY);
                    Trace.WriteLine("**** SEE http://www.inguzaudio.com/DSP/ FOR DETAILS");
                    _MatrixConvolver.Enabled = false;
                    _MainConvolver.Enabled = false;
                    _EQConvolver.Enabled = false;
                    Show("", "InguzDSP has expired.", 2);
                }
                else
                {
                    Trace.WriteLine("This evaluation version will expire {0}", DSPUtil.DSPUtil.EXPIRY);
                }
            }
            */

            // Read the configuration file
            doRun = LoadConfig2();

            // Do any cleanup required before we start
            CleanUp();

            // The main convolver should persist and re-use its leftovers between invocations
            // under this user's (=squeezebox's) ID
            _MainConvolver.partitions = _partitions;
            if (_tail && !IsSigGenNonEQ())
            {
                _MainConvolver.PersistPath = _tempFolder;
                _MainConvolver.PersistTail = _userID;
            }

            // Construct a second convolver for the "tone" (EQ) control.
            _EQConvolver.partitions = _partitions;
            if (_tail && !IsSigGenNonEQ())
            {
                _EQConvolver.PersistPath = _tempFolder;
                _EQConvolver.PersistTail = _userID + ".eq";
            }


            // Make a reader
            // _inPath is the data stream
            WaveReader inputReader = null;
            bool ok = false;
            try
            {
                if (_inStream != null)
                {
                    inputReader = new WaveReader(_inStream);
                }
                else if (_isRawIn)
                {
                    inputReader = new WaveReader(_inPath, _rawtype, _rawbits, _rawchan, _startTime);
                }
                else
                {
                    inputReader = new WaveReader(_inPath, _startTime);
                }
                inputReader.BigEndian = _bigEndian;
                ok = true;
            }
            catch (Exception e)
            {
                Trace.WriteLine("Unable to read: " + e.Message);
                // Just stop (no need to report the stack)
            }

            if (ok)
            {
                if (inputReader.IsSPDIF)
                {
                    // The wave file is just a SPDIF (IEC61937) stream, we shouldn't touch it
                    _inputSPDIF = true;
                    _isBypass = true;
                }
                if (_isBypass)
                {
                    // The settings file says bypass, we shouldn't touch it
                    _gain = 0;
                    _dither = DitherType.NONE;
                }

                uint sr = _inputSampleRate; // Yes, the commandline overrides the source-file...
                if (sr == 0)
                {
                    sr = inputReader.SampleRate;
                }
                if (sr == 0)
                {
                    sr = 44100;
                }
                _inputSampleRate = sr;

                if (WaveFormatEx.AMBISONIC_B_FORMAT_IEEE_FLOAT.Equals(inputReader.FormatEx) ||
                    WaveFormatEx.AMBISONIC_B_FORMAT_PCM.Equals(inputReader.FormatEx))
                {
                    _isBFormat = true;
                }

            }

            ISoundObj source = inputReader;
            if (IsSigGen())
            {
                // Signal-generator source instead of music.
                _isBFormat = false;
                source = GetSignalGenerator(-12, out sigdesc);
                Show("Test signal", sigdesc, 20);
            }

            if (IsSigGenNonEQ() || _isBypass)
            {
                // Signal-generator mode.  Overrides everything else!
                _writer.Input = source;
            }
            else
            {
                if (ok)
                {
                    // Load the room correction impulse to the convolver
                    // (NB: don't do this until we've created the reader, otherwise we can't be user of the samplerate yet...)
                    LoadImpulse();
                    GC.Collect();
                }

                if (ok && _isBFormat)
                {
                    source = DecodeBFormat(source);
                }

                if (ok)
                {
                    ISoundObj nextSrc;

                    // Perform width (matrix) processing on the signal
                    // - Shuffle the channels
                    // - Convolve if there's a filter
                    // - Apply gain to boost or cut the 'side' channel
                    // - Shuffle back
                    _widthShuffler.Input = source;
                    nextSrc = _widthShuffler;

                    // Use a convolver for the matrix filter
                    if (_matrixFilter != null)
                    {
                        LoadMatrixFilter();
                        _MatrixConvolver.Input = TwoChannel(nextSrc);
                        nextSrc = _MatrixConvolver as ISoundObj;
                    }

                    //                if (_depth != 0)
                    //                {
                    //                    // Time-alignment between the LR and MS
                    //                    _depthSkewer.Input = nextSrc;
                    //                    nextSrc = _depthSkewer;
                    //                }

                    // Shuffle back again
                    Shuffler shMSLR = new Shuffler();
                    shMSLR.Input = nextSrc;
                    nextSrc = shMSLR;

                    // Do the room-correction convolution
                    _MainConvolver.Input = TwoChannel(shMSLR);
                    nextSrc = _MainConvolver;

                    if (_skew != 0)
                    {
                        // time-alignment between left and right
                        _skewSkewer.Input = nextSrc;
                        nextSrc = _skewSkewer;
                    }

                    // Splice EQ and non-EQ channels
                    if (IsSigGenEQ())
                    {
                        ChannelSplicer splice = new ChannelSplicer();
                        if (IsSigGenEQL())
                        {
                            splice.Add(new SingleChannel(nextSrc, 0));
                        }
                        else
                        {
                            splice.Add(new SingleChannel(source, 0));
                        }
                        if (IsSigGenEQR())
                        {
                            splice.Add(new SingleChannel(nextSrc, 1));
                        }
                        else
                        {
                            splice.Add(new SingleChannel(source, 1));
                        }
                        nextSrc = splice;
                    }

                    // Process externally with aften or equivalent?
                    if (_aftenNeeded && !_isBypass)
                    {
                        nextSrc = AftenProcess(nextSrc);
                        _outFormat = WaveFormat.PCM;
                        _outBits = 16;
                        _dither = DitherType.NONE;
                    }

                    // Finally pipe this to the writer
                    _writer.Input = nextSrc;
                }
            }
            if (ok)
            {
                //dt = System.DateTime.Now;       // time to here is approx 300ms


                // Dither and output raw-format override anything earlier in the chain
                _writer.Dither = _isBypass ? DitherType.NONE : _dither;
                _writer.Raw = _isRawOut;
                _writer.Format = (_outFormat == WaveFormat.ANY) ? inputReader.Format : _outFormat;
                _writer.BitsPerSample = (_outBits == 0 || _isBypass) ? inputReader.BitsPerSample : _outBits;
                _writer.SampleRate = (_outRate == 0 || _isBypass) ? _inputSampleRate : _outRate;
                SetWriterGain();
                if (IsSigGen())
                {
                    Trace.WriteLine("Test signal: {0}, -12dBfs, {1}/{2} {3} {4}", sigdesc, _writer.BitsPerSample, _writer.SampleRate, _writer.Format, _writer.Dither);
                }
                string amb1 = "";
                string amb2 = "";
                if(_isBFormat)
                {
                    amb1 = "B-Format ";
                    amb2 = _ambiType + " ";
                }
                string big = inputReader.BigEndian ? "(Big-Endian) " : "";
                if (_inputSPDIF)
                {
                    Trace.WriteLine("Stream is SPDIF-wrapped; passing through");
                }
                else if (_isBypass)
                {
                    Trace.WriteLine("Processing is disabled; passing through");
                }
                Trace.WriteLine("{0}/{1} {2}{3} {4}=> {5}/{6} {7}{8} {9}, gain {10} dB", inputReader.BitsPerSample, _inputSampleRate, amb1, inputReader.Format, big, _writer.BitsPerSample, _writer.SampleRate, amb2, _writer.Format, _writer.Dither, _gain);

                TimeSpan elapsedInit = System.DateTime.Now.Subtract(dtStartRun);
                int n = _writer.Run();

                TimeSpan elapsedTotal = System.DateTime.Now.Subtract(dtStartRun);
                double realtime = n / _writer.SampleRate;
                double runtime = elapsedTotal.TotalMilliseconds / 1000;
                Trace.WriteLine("{0} samples, {1} ms ({2} init), {3} * realtime, peak {4} dBfs", n, elapsedTotal.TotalMilliseconds, elapsedInit.TotalMilliseconds, Math.Round(realtime / runtime, 4), Math.Round(_writer.dbfsPeak, 4));

                StopConfigListening();

                _writer.Close();
            }
        }

        static string CleanPath(string basePath, string fullPath)
        {
            if (fullPath == null) return "(null)";
            return fullPath.Replace(basePath, "");
        }

        static ISoundObj TwoChannel(ISoundObj src)
        {
            if (src.NumChannels == 2)
            {
                return src;
            }
            else if (src.NumChannels == 1)
            {
                ChannelSplicer splicer = new ChannelSplicer();
                splicer.Add(src);
                splicer.Add(src);
                return splicer;
            }
            else
            {
                ChannelSplicer splicer = new ChannelSplicer();
                splicer.Add(new SingleChannel(src, 0));
                splicer.Add(new SingleChannel(src, 1));
                return splicer;
            }
        }


        #region Signal Generator
        static bool IsSigGen()
        {
            return (_siggen != null);
        }
        static bool IsSigGenEQ()
        {
            return (_siggen != null) && _siggenUseEQ!=ChannelFlag.NONE;
        }
        static bool IsSigGenEQL()
        {
            return (_siggen != null) && ((_siggenUseEQ & ChannelFlag.LEFT) == ChannelFlag.LEFT);
        }
        static bool IsSigGenEQR()
        {
            return (_siggen != null) && ((_siggenUseEQ & ChannelFlag.RIGHT) == ChannelFlag.RIGHT);
        }
        static bool IsSigGenEQBoth()
        {
            return (_siggen != null) && ((_siggenUseEQ & ChannelFlag.BOTH) == ChannelFlag.BOTH);
        }
        static bool IsSigGenNonEQ()
        {
            return (_siggen != null) && _siggenUseEQ==ChannelFlag.NONE;
        }
        static bool IsSigGenNonEQL()
        {
            return (_siggen != null) && ((_siggenUseEQ & ChannelFlag.LEFT) != ChannelFlag.LEFT);
        }
        static bool IsSigGenNonEQR()
        {
            return (_siggen != null) && ((_siggenUseEQ & ChannelFlag.RIGHT) != ChannelFlag.RIGHT);
        }

        static ISoundObj GetSignalGenerator(double dBfs, out string desc)
        {
            double gain = MathUtil.gain(dBfs);
            ISoundObj signalGenerator = null;
            Sequencer seq;
            string description = "Unknown";
            switch (_siggen)
            {
                case "IDENT":
                    // Left-right identification: embedded resource
                    Assembly ass = Assembly.GetExecutingAssembly();
                    foreach (string s in ass.GetManifestResourceNames())
                    {
                        if (s.Contains("LeftRight"))
                        {
                            Stream res = ass.GetManifestResourceStream(s);
                            WaveReader rdr = new WaveReader(res);
                            // The stream is stereo, but we want to alternate
                            seq = new Sequencer();

                            for (int j = 0; j < 10; j++)
                            {
                                seq.Add(rdr, new List<double>(new double[] { gain, 0 }));
                                seq.Add(new NoiseGenerator(NoiseType.SILENCE, 2, 1.0, _inputSampleRate, 0.0, false));
                                seq.Add(rdr, new List<double>(new double[] { 0, gain }));
                                seq.Add(new NoiseGenerator(NoiseType.SILENCE, 2, 1.0, _inputSampleRate, 0.0, false));
                            }

                            signalGenerator = seq;
                            break;
                        }
                    }
                    /*
                    // Left-right identification signal: morse code
                    MorseCode envL = new MorseCode(" " + _sigparamA, 10, true);
                    ISoundObj sigL = new SweepGenerator(1, envL.LengthSeconds * 5, 220, 7040, _inputSampleRate, 0, false, gain, true);
                    envL.Input = sigL;

                    MorseCode envR = new MorseCode(" " + _sigparamB, 10, true);
                    ISoundObj sigR = new SweepGenerator(1, envR.LengthSeconds * 5, 7040, 220, _inputSampleRate, 0, false, gain, true);
                    envR.Input = sigR;

                    signalGenerator = new ChannelSplicer();
                    (signalGenerator as ChannelSplicer).Add(envL);
                    (signalGenerator as ChannelSplicer).Add(envR);
                    */
                    description = String.Format("Left/Right channel identification");
                    break;

                case "SWEEP":
                    seq = new Sequencer();
                    if (_sigparam1 == 0)
                    {
                        _sigparam1 = 45;
                    }
                    int lengthSamples = (int)(_sigparam1 * _inputSampleRate);
                    if (lengthSamples < 8388608)
                    {
                        // High-accuracy logarithmic sweep starting at 10Hz
                        int fade = (int)(_inputSampleRate / 20);
                        FFTSweepGenerator sg = new FFTSweepGenerator(2, lengthSamples, 10, _inputSampleRate / 2, _inputSampleRate, gain, false);
                        seq.Add(sg);
                        description = String.Format("Logarithmic sine sweep 10Hz to {0}Hz in {1} seconds", _inputSampleRate / 2, _sigparam1);
                    }
                    else
                    {
                        // Simple logarithmic sweep starting at 10Hz, windowed (uses much less memory!)
                        int fade = (int)(_inputSampleRate / 20);
                        BlackmanHarris bhwf = new BlackmanHarris(lengthSamples / 2, fade, (int)((lengthSamples / 2) - fade));
                        SweepGenerator sg = new SweepGenerator(2, lengthSamples, 10, _inputSampleRate / 2, _inputSampleRate, gain, false);
                        bhwf.Input = sg;
                        seq.Add(bhwf);
                        description = String.Format("Log sine sweep 10Hz to {0}Hz in {1} seconds", _inputSampleRate / 2, _sigparam1);
                    }
                    // Follow by 3 seconds of silence
                    seq.Add(new NoiseGenerator(NoiseType.SILENCE, 2, 3.0, _inputSampleRate, 0.0, false));
                    signalGenerator = seq;
                    break;

                case "SINE":
                    signalGenerator = new SineGenerator(2, _inputSampleRate, _sigparam1, gain);
                    description = String.Format("{0}Hz sine", _sigparam1);
                    break;

                case "QUAD":
                    signalGenerator = new SineQuadGenerator(2, _inputSampleRate, _sigparam1, gain);
                    description = String.Format("{0}Hz quadrature", _sigparam1);
                    break;

                case "SQUARE":
                    signalGenerator = new SquareGenerator(2, _inputSampleRate, _sigparam1, gain);
                    description = String.Format("{0}Hz non-bandlimited square", _sigparam1);
                    break;

                case "BLSQUARE":
                    signalGenerator = new BandLimitedSquareGenerator(2, _inputSampleRate, _sigparam1, gain);
                    description = String.Format("{0}Hz bandlimited square", _sigparam1);
                    break;

                case "TRIANGLE":
                    signalGenerator = new TriangleGenerator(2, _inputSampleRate, _sigparam1, gain);
                    description = String.Format("{0}Hz non-bandlimited triangle", _sigparam1);
                    break;

                case "BLTRIANGLE":
                    signalGenerator = new BandLimitedTriangleGenerator(2, _inputSampleRate, _sigparam1, gain);
                    description = String.Format("{0}Hz bandlimited triangle", _sigparam1);
                    break;

                case "SAWTOOTH":
                    signalGenerator = new SawtoothGenerator(2, _inputSampleRate, _sigparam1, gain);
                    description = String.Format("{0}Hz non-bandlimited sawtooth", _sigparam1);
                    break;

                case "BLSAWTOOTH":
                    signalGenerator = new BandLimitedSawtoothGenerator(2, _inputSampleRate, _sigparam1, gain);
                    description = String.Format("{0}Hz bandlimited sawtooth", _sigparam1);
                    break;

                case "WHITE":
                    signalGenerator = new NoiseGenerator(NoiseType.WHITE, 2, int.MaxValue, _inputSampleRate, gain, true);
                    description = String.Format("White noise");
                    break;

                case "PINK":
                    bool mono = (_sigparam1 != 0 ? true : false);
                    signalGenerator = new NoiseGenerator(NoiseType.PINK, 2, int.MaxValue, _inputSampleRate, gain, mono);
                    description = String.Format("Pink noise {0}", mono ? "(mono)" : "(stereo)" );
                    break;

                case "INTERMODULATION":
                    double n = 1;
                    description = String.Format("Intermodulation test {0}Hz", _sigparam1);
                    if (_sigparam2 != 0) { n++; description = description + " + " + _sigparam2 + "Hz"; }
                    if (_sigparam3 != 0) { n++; description = description + " + " + _sigparam3 + "Hz"; }
                    signalGenerator = new Mixer();
                    (signalGenerator as Mixer).Add(new SineGenerator(2, _inputSampleRate, _sigparam1, gain), 1/n);
                    if (_sigparam2 != 0) (signalGenerator as Mixer).Add(new SineGenerator(2, _inputSampleRate, _sigparam2, gain), 1 / n);
                    if (_sigparam3 != 0) (signalGenerator as Mixer).Add(new SineGenerator(2, _inputSampleRate, _sigparam3, gain), 1 / n);
                    break;

                case "SHAPEDBURST":
                    description = String.Format("{0}Hz windowed (Blackman) over {1} cycles", _sigparam1, _sigparam2);
                    throw new NotImplementedException();
                    //break;

                default:
                    _siggen = null;
                    break;
            }
            if (IsSigGenEQ())
            {
                if (IsSigGenEQBoth())
                {
                    description = description + ", with EQ processing";
                }
                else if (IsSigGenEQL())
                {
                    description = description + ", with EQ processing in left channel";
                }
                else if (IsSigGenEQR())
                {
                    description = description + ", with EQ processing in right channel";
                }
                else
                {
                    description = description + ", with EQ processing";
                }
            }
            desc = description;
            return signalGenerator;
        }
        #endregion


        #region Configuration

        static FileSystemWatcher _configWatcher;
        static readonly Object _lock = new Object();
        static Thread _configReaderThread;
        static ManualResetEvent _configReaderEvent = new ManualResetEvent(false);
        static bool _stopNow = false;

        static void StartConfigListening()
        {
            // Create a filesystem watcher for the config file
            System.IO.FileInfo f = new FileInfo(_configFile);
            _configWatcher = new FileSystemWatcher(f.DirectoryName, "*.settings.conf");
            _configWatcher.Changed += new FileSystemEventHandler(OnConfigurationChanged);
            _configWatcher.EnableRaisingEvents = true;
            //          Trace.WriteLine("Listening for configuration changes.");
        }

        static void StopConfigListening()
        {
            if (_configWatcher != null)
            {
                _configWatcher.EnableRaisingEvents = false;
                // If there's a thread handling config update reads, tell it to go away now
                lock (_lock)
                {
                    if (_configReaderThread != null)
                    {
                        _stopNow = true;
                        _configReaderThread.Abort();
                    }
                }
            }
        }

        static void OnConfigurationChanged(object source, FileSystemEventArgs e)
        {
            // Configuration file has changed.
            // If we can, reload the file and use the new settings.
            // (If we can't read the new file, fail silently, that's OK)
            if (_debug)
            {
                Trace.WriteLine("File {0}: {1}", e.ChangeType, CleanPath(_settingsFolder, e.FullPath));
            }
            lock (_lock)
            {
                if (_configReaderThread == null)
                {
                    // Start a new thread to read the configuration file whenever it can
                    _configReaderThread = new Thread(ConfigReaderThread);
                    _configReaderThread.Start();
                }
                else
                {
                    // Tell the reader thread it has work to do
                    _configReaderEvent.Set();
                }
            }
        }

        static void ConfigReaderThread()
        {
            while (!_stopNow)
            {
                bool ok = false;
                try
                {
                    // Wait (for de-bounce, and so we don't chew *all* the CPU when config changes need EQ recalc)
                    Thread.Sleep(250);
                    if (_debug)
                    {
                        Trace.WriteLine("Configuration changed");
                    }
                    _configReaderEvent.Reset();
                    TryReadConfig(false);
                    ok = true;
                }
                catch (Exception e)
                {
                    if (_debug)
                    {
                        Trace.WriteLine("ConfigReaderThread: " + e.Message);
                    }
                }
                if (!_stopNow)
                {
                    try
                    {
                        if (ok)
                        {
                            // Wait until someone wakes us up
                            _configReaderEvent.WaitOne();
                        }
                        else
                        {
                            // Try again shortly
                            _configReaderEvent.WaitOne(500, false);
                        }
                    }
                    catch (Exception e)
                    {
                        if (_debug)
                        {
                            Trace.WriteLine("ConfigReaderThread2: " + e.Message);
                        }
                    }
                }
            }
        }

        // -----

        static bool LoadConfig1()
        {
            // Some settings are basically obsolete
            // - never configurable by the client-settings document
            // - but we still allow override from app.config
            // Load them here (rather than in ReadConfig) because the appsettings is always cached anyway...
            // even if we watched it, it would never change...

            System.Configuration.AppSettingsReader rdr;
            try
            {
                rdr = new System.Configuration.AppSettingsReader();
            }
            catch (Exception e)
            {
                Trace.WriteLine("AppSettings error: {0}", e.Message); 
                return true;
            }

            try { _debug |= (bool)rdr.GetValue("debug", typeof(bool)); }
            catch (Exception) { }
            try { _inPath = (string)rdr.GetValue("input", typeof(string)); }
            catch (Exception) { }
            try { _isRawIn = (bool)rdr.GetValue("rawIn", typeof(bool)); }
            catch (Exception) { }
            try { _rawtype = (WaveFormat)rdr.GetValue("rawtype", typeof(int)); }
            catch (Exception) { }
            try { _rawbits = (ushort)rdr.GetValue("rawbits", typeof(ushort)); }
            catch (Exception) { }
            try { _rawchan = (ushort)rdr.GetValue("rawchan", typeof(ushort)); }
            catch (Exception) { }

            try { _outPath = (string)rdr.GetValue("output", typeof(string)); }
            catch (Exception) { }
            try { _outBits = (ushort)rdr.GetValue("outbits", typeof(ushort)); }
            catch (Exception) { }

            try { _dither = (DitherType)rdr.GetValue("dither", typeof(int)); }
            catch (Exception) { }
            try { _isRawOut = (bool)rdr.GetValue("rawOut", typeof(bool)); }
            catch (Exception) { }
            try { _partitions = (int)rdr.GetValue("partitions", typeof(int)); }
            catch (Exception) { }

            try { _maxImpulseLength = (int)rdr.GetValue("maximpulse", typeof(int)); }
            catch (Exception) { }

            try { _tail = (bool)rdr.GetValue("tail", typeof(bool)); }
            catch (Exception) { }

            try { _slow = (bool)rdr.GetValue("slow", typeof(bool)); }
            catch (Exception) { }

            // sox settings
            try { _soxExe = (string)rdr.GetValue("soxExe", typeof(string)); }
            catch (Exception) { }

            try { _soxFmt = (string)rdr.GetValue("soxFmt", typeof(string)); }
            catch (Exception) { }

            // aften (or other output device) settings
            try { _aftenExe = (string)rdr.GetValue("aftenExe", typeof(string)); }
            catch (Exception) { }

            try { _aftenFmt = (string)rdr.GetValue("aftenFmt", typeof(string)); }
            catch (Exception) { }

            // Ambisonic settings
            try { _ambiDistance = (double)rdr.GetValue("ambiDistance", typeof(double)); }
            catch (Exception) { }

            try { _ambiShelfFreq = (double)rdr.GetValue("ambiShelfFreq", typeof(double)); }
            catch (Exception) { }

            return true;
        }

        private static bool LoadConfig2()
        {
            // All the processing objects are already initialized when this is called

            // Mostly everything is in the conf document
            // and can change at runtime.


            // The client's configuration file is XML, named
            // <client_ID_with_underscores_replacing_colons>.settings.conf
            // in
            // <C:\Documents and Settings\All Users\Application Data\InguzEQ\Settings>
            //
            string fileName = _userID.Replace(':', '_') + ".settings.conf";
            _configFile = Path.Combine(_settingsFolder, fileName);

            bool ok = false;
            if (!System.IO.File.Exists(_configFile))
            {
                Trace.WriteLine("The settings file '{0}' was not found.  Using defaults.", _configFile);
            }
            else
            {
                // Now read it
                ok = ReadConfig(true);

                // Listen for configuration changes while we run
                StartConfigListening();
            }

            // Gain can maybe be changed at runtime
            // (but doesn't apply to signal generator mode)
            System.Configuration.AppSettingsReader rdr;
            try
            {
                rdr = new System.Configuration.AppSettingsReader();
            }
            catch (Exception e)
            {
                Trace.WriteLine("AppSettings error: {0}", e.Message);
                return true;
            }
            try
            {
                _gain = (double)rdr.GetValue("gain", typeof(double));
            }
            catch (Exception) { /* ignore */ }

            if (_siggen == null && _writer != null && !_isBypass)
            {
                Trace.WriteLine("Gain {0} dB", _gain);
                SetWriterGain();
            }

            return ok;
        }

        private static void SetWriterGain()
        {
            if (_writer != null)
            {
                if (_isBypass)
                {
                    _writer.Gain = double.NaN;
                    for (ushort c = 0; c < _writer.NumChannels; c++)
                    {
                        _writer.SetChannelGain(c, double.NaN);
                    }
                }
                else
                {
                    _writer.Gain = double.NaN;

                    double g = MathUtil.gain(_gain);

                    bool flatLeft = IsSigGenNonEQL();       // Signal generator, without EQ in the left channel
                    bool flatRight = IsSigGenNonEQR();

                    // Balance is dB difference between left and right channels (positive:right)
                    // e.g. +6 makes the left channel -3dB and right channel +3dB
                    int chL = 0;
                    int chR = 0;
                    double fracBal = _balance / 2;
                    double gainL = g;
                    double gainR = g;
                    if(_impulseVolumes.Count==1)
                    {
                        gainL = (_impulseVolumes[chL] == 0 ? g : (g / _impulseVolumes[chL]));
                        gainR = (_impulseVolumes[chL] == 0 ? g : (g / _impulseVolumes[chL]));
                    }
                    else if(_impulseVolumes.Count>=2)
                    {
                        gainL = (_impulseVolumes[chL] == 0 ? g : (g / _impulseVolumes[chL]));
                        gainR = (_impulseVolumes[chR] == 0 ? g : (g / _impulseVolumes[chR]));
                    }
                    gainL *= MathUtil.gain(-fracBal);
                    gainR *= MathUtil.gain(fracBal);

                    _writer.SetChannelGain(0, flatLeft ? double.NaN : gainL);
                    if (_writer.NumChannels > 1)
                    {
                        _writer.SetChannelGain(1, flatRight ? double.NaN : gainR);
                    }
                }
            }
        }

        private static bool ReadConfig(bool firstTime)
        {
            // Load the config document.  If anything fails, KEEP GOING!
            try
            {
                return TryReadConfig(firstTime);
            }
            catch (Exception e)
            {
                Trace.WriteLine("Cannot load settings {0}: {1}", CleanPath(_settingsFolder, _configFile), e.Message);
                return false;
            }
        }

        static string nodeText(XmlDocument doc, string xpath)
        {
            XmlNode node = doc.SelectSingleNode(xpath);
            return (node == null) ? null : node.InnerText;
        }

        static string nodeValue(XmlDocument doc, string xpath)
        {
            XmlNode node = doc.SelectSingleNode(xpath);
            return (node == null) ? null : node.Value;
        }

        static string nodeValueUpper(XmlDocument doc, string xpath)
        {
            XmlNode node = doc.SelectSingleNode(xpath);
            return (node == null) ? null : node.Value.ToUpperInvariant();
        }

        static int nodeValueInt(XmlDocument doc, string xpath)
        {
            XmlNode node = doc.SelectSingleNode(xpath);
            return (node == null) ? 0 : int.Parse(node.Value, CultureInfo.InvariantCulture);
        }

        static double nodeValueDouble(XmlDocument doc, string xpath)
        {
            XmlNode node = doc.SelectSingleNode(xpath);
            return (node == null) ? 0 : double.Parse(node.Value, CultureInfo.InvariantCulture);
        }

        static bool nodeValueBool(XmlDocument doc, string xpath)
        {
            XmlNode node = doc.SelectSingleNode(xpath);
            if(node == null)
            {
                return false;
            }
            try
            {
                return bool.Parse(node.Value);
            }
            catch (Exception) { }
            return false;
        }

        // Compatiblity-mode for reading loudness and flatness values
        // supporting two formats:
        // - a number in the range 0 through 10 --> double in range 0 through 100 (e.g. "9"->90)
        // - a number with a percent sign --> double in range 0 through 001 (e.g. "90%"->90)
        static double nodeValuePercentage(XmlDocument doc, string xpath, double defaultValue)
        {
            double val = defaultValue;
            XmlNode node = doc.SelectSingleNode(xpath);
            if (node != null)
            {
                try
                {
                    string s = node.InnerText;
                    if (s.Contains("%"))
                    {
                        s = s.Remove(s.IndexOf('%'));
                        val = double.Parse(s, CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        val = double.Parse(s, CultureInfo.InvariantCulture) * 10;
                    }
                }
                catch (Exception) { }
            }
            return val;
        }


        static bool TryReadConfig(bool firstTime)
        {
            lock (_lockReadConfig)
            {
                XmlNode node;
                _configDocument.Load(_configFile);

                int oldDepth = _depth;
                string oldImpulsePath = _impulsePath;
                int oldBands = _eqBands;
                double oldLoudness = _eqLoudness;
                double oldFlatness = _eqFlatness;
                string oldMatrixFilter = _matrixFilter;
                string oldBFormatFilter = _bformatFilter;
                FilterProfile oldValues = new FilterProfile(_eqValues);

                _isBypass = nodeValueBool(_configDocument, "//InguzEQSettings/Client/@Bypass");

                // SignalGenerator mode: if there's a "SignalGenerator" element it overrides normal processing.
                _siggen = null;
                node = _configDocument.SelectSingleNode("//InguzEQSettings/Client/SignalGenerator");
                if (node != null)
                {
                    _siggenUseEQ = ChannelFlag.NONE;
                    string useEQ = nodeValue(_configDocument, "//InguzEQSettings/Client/SignalGenerator/@UseEQ");
                    if (!String.IsNullOrEmpty(useEQ))
                    {
                        switch (useEQ.ToUpperInvariant())
                        {
                            case "1":
                            case "YES":
                            case "TRUE":
                            case "BOTH":
                                _siggenUseEQ = ChannelFlag.BOTH;
                                break;
                            case "L":
                                _siggenUseEQ = ChannelFlag.LEFT;
                                break;
                            case "R":
                                _siggenUseEQ = ChannelFlag.RIGHT;
                                break;
                        }
                    }

                    _siggen = nodeValueUpper(_configDocument, "//InguzEQSettings/Client/SignalGenerator/@Type");
                    if (_siggen != null)
                    {
                        switch (_siggen)
                        {
                            case "IDENT":
                                _sigparamA = nodeValue(_configDocument, "//InguzEQSettings/Client/SignalGenerator/@L");
                                _sigparamB = nodeValue(_configDocument, "//InguzEQSettings/Client/SignalGenerator/@R");
                                break;
                            case "SWEEP":
                                _sigparam1 = nodeValueInt(_configDocument, "//InguzEQSettings/Client/SignalGenerator/@Length");
                                break;
                            case "SINE":
                            case "QUAD":
                            case "SQUARE":
                            case "BLSQUARE":
                            case "TRIANGLE":
                            case "BLTRIANGLE":
                            case "SAWTOOTH":
                            case "BLSAWTOOTH":
                                _sigparam1 = nodeValueInt(_configDocument, "//InguzEQSettings/Client/SignalGenerator/@Freq");
                                break;
                            case "WHITE":
                                break;
                            case "PINK":
                                // "Mono" param defaults to true for backward compatibility
                                _sigparam1 = 1;
                                string mono = nodeValue(_configDocument, "//InguzEQSettings/Client/SignalGenerator/@Mono");
                                if (!String.IsNullOrEmpty(mono))
                                {
                                    _sigparam1 = bool.Parse(mono) ? 1 : 0;
                                }
                                break;
                            case "INTERMODULATION":
                                _sigparam1 = nodeValueInt(_configDocument, "//InguzEQSettings/Client/SignalGenerator/@Freq1");
                                _sigparam2 = nodeValueInt(_configDocument, "//InguzEQSettings/Client/SignalGenerator/@Freq2");
                                _sigparam3 = nodeValueInt(_configDocument, "//InguzEQSettings/Client/SignalGenerator/@Freq3");
                                break;
                            case "SHAPEDBURST":
                                _sigparam1 = nodeValueInt(_configDocument, "//InguzEQSettings/Client/SignalGenerator/@Freq");
                                _sigparam2 = nodeValueInt(_configDocument, "//InguzEQSettings/Client/SignalGenerator/@Cycles");
                                break;
                            default:
                                _siggen = null;
                                break;
                        }
                    }
                }

                // Impulse path can be changed at runtime. Null or "" or "-" are all OK (null) values.
                node = _configDocument.SelectSingleNode("//InguzEQSettings/Client/Filter"); // @URL");
                _impulsePath = node == null ? "" : node.InnerText;

                // Same for stereo-width ('matrix') filter
                node = _configDocument.SelectSingleNode("//InguzEQSettings/Client/Matrix"); // @URL");
                _matrixFilter = node == null ? "" : node.InnerText;

                // Same for ambisonic filter (not documented)
                node = _configDocument.SelectSingleNode("//InguzEQSettings/Client/BFormat"); // @URL");
                _bformatFilter = node == null ? "" : node.InnerText;

                node = _configDocument.SelectSingleNode("//InguzEQSettings/Client/EQ/@Bands");
                _eqBands = (node == null) ? 0 : int.Parse(node.Value, CultureInfo.InvariantCulture);

                _eqValues.Clear();
                XmlNodeList nodes = _configDocument.SelectNodes("//InguzEQSettings/Client/EQ/Band");
                if (nodes != null)
                {
                    foreach (XmlNode bnode in nodes)
                    {
                        string att;
                        att = ((XmlElement)bnode).GetAttribute("Freq");
                        double freq = (att == null) ? 0 : double.Parse(att, CultureInfo.InvariantCulture);
                        if (freq >= 10 && freq < 48000)
                        {
                            att = ((XmlElement)bnode).InnerText; //.GetAttribute("dB");
                            double gain = (att == null) ? 0 : double.Parse(att, CultureInfo.InvariantCulture);
                            if (gain < -20) gain = -20;
                            if (gain > 20) gain = 20;
                            _eqValues.Add(new FreqGain(freq, gain));
                        }
                    }
                }

                _eqLoudness = nodeValuePercentage(_configDocument, "//InguzEQSettings/Client/Quietness", 0);

                _eqFlatness = nodeValuePercentage(_configDocument, "//InguzEQSettings/Client/Flatness", 100);

                node = _configDocument.SelectSingleNode("//InguzEQSettings/Client/Width");
                _width = (node == null) ? 0 : double.Parse(node.InnerText, CultureInfo.InvariantCulture);
                double fracWid = _width / 2;
                _widthShuffler.SigmaGain = MathUtil.gain(-fracWid);
                _widthShuffler.DeltaGain = MathUtil.gain(fracWid);     // UI specifies width in dB

                // Depth not documented (and not really working either)
                node = _configDocument.SelectSingleNode("//InguzEQSettings/Client/Depth");
                _depth = (node == null) ? 0 : int.Parse(node.InnerText, CultureInfo.InvariantCulture);
//              _depthSkewer.Skew = _depth;

                node = _configDocument.SelectSingleNode("//InguzEQSettings/Client/Balance");
                _balance = (node == null) ? 0 : double.Parse(node.InnerText, CultureInfo.InvariantCulture);
                SetWriterGain();

                node = _configDocument.SelectSingleNode("//InguzEQSettings/Client/Skew");
                _skew = (node == null) ? 0 : int.Parse(node.InnerText, CultureInfo.InvariantCulture);
                _skewSkewer.Skew = _skew;

                // <AmbisonicDecode Type="XXX" Cardioid="N" Angle="N" File="path">
                //
                // For 2ch Ambisonic decodes (stereo UHJ, stereo Blumlein, etc), the type is specified in a parameter
                _ambiType = nodeValue(_configDocument, "//InguzEQSettings/Client/AmbisonicDecode/@Type");
                if (!String.IsNullOrEmpty(_ambiType) && _ambiType.ToUpperInvariant() == "MATRIX")
                {
                    _aftenNeeded = true;
                }
                _ambiCardioid = nodeValueDouble(_configDocument, "//InguzEQSettings/Client/AmbisonicDecode/@Cardioid");
                _ambiMicAngle = nodeValueDouble(_configDocument, "//InguzEQSettings/Client/AmbisonicDecode/@Angle");

                _ambiRotateX = nodeValueDouble(_configDocument, "//InguzEQSettings/Client/AmbisonicDecode/@RotateX");
                _ambiRotateY = nodeValueDouble(_configDocument, "//InguzEQSettings/Client/AmbisonicDecode/@RotateY");
                _ambiRotateZ = nodeValueDouble(_configDocument, "//InguzEQSettings/Client/AmbisonicDecode/@RotateZ");

                // For other Ambisonic decodes, the full decode matrix
                // is defined in a file, specified in config
                _ambiMatrixFile = nodeText(_configDocument, "//InguzEQSettings/Client/AmbisonicDecode/@File");

                if (!firstTime)
                {
                    bool eqChanged = false;
                    if ((_eqBands != oldBands) || (_eqLoudness != oldLoudness) || (_eqFlatness != oldFlatness))
                    {
                        eqChanged = true;
                    }
                    else
                    {
                        for (int n = 0; n < _eqValues.Count; n++)
                        {
                            if (_eqValues[n].Gain != oldValues[n].Gain || _eqValues[n].Gain != oldValues[n].Gain)
                            {
                                eqChanged = true;
                                break;
                            }
                        }
                    }
                    if (eqChanged || (oldImpulsePath != _impulsePath))
                    {
                        // Notify the convolver that there's a new impulse file
                        LoadImpulse();
                    }
                    if ((oldMatrixFilter != _matrixFilter) || (_depth != oldDepth))
                    {
                        LoadMatrixFilter();
                    }
                    if (oldBFormatFilter != _bformatFilter)
                    {
                        LoadBFormatFilter();
                    }
                }
                return true;
            }
        }

        #endregion

        #region aften for external AC3 encoding
        static ISoundObj AftenProcess(ISoundObj input)
        {
            // _aftenExe = "aften";
            string exeName = _aftenExe;
            if (File.Exists(Path.Combine(_pluginFolder, _aftenExe + ".exe")))
            {
                exeName = "\"" + Path.Combine(_pluginFolder, _aftenExe + ".exe") + "\"";
            }
            else if (File.Exists(Path.Combine(_pluginFolder, _aftenExe)))
            {
                exeName = Path.Combine(_pluginFolder, _aftenExe);
            }

            SPDIFWrappedExternal aften = new SPDIFWrappedExternal(exeName, _aftenFmt);
            aften.Input = input;
            aften.Dither = _dither;
            aften.Format = WaveFormat.PCM;
            aften.BitsPerSample = 16;
            // TBD: gain will be WRONG and will break when SetWriterGain() is called

            return aften;
        }
        #endregion

        static void LoadImpulse()
        {
            DateTime dtStart = DateTime.Now;
            string theImpulsePath = null;
            string theEQImpulseName = null;
            ISoundObj main = GetMainImpulse(out theImpulsePath);
            uint sr = (main == null ? _inputSampleRate : main.SampleRate);
            if (sr == 0)
            {
                if (_debug) { Trace.WriteLine("oops: no sample rate!"); }
                sr = 44100;
            }
            ISoundObj eq = GetEQImpulse(main, sr, out theEQImpulseName);
            ISoundObj combinedFilter = null;

            if (main == null && eq != null)
            {
                combinedFilter = eq;
            }
            else if (main != null && eq == null)
            {
                combinedFilter = main;
            }
            else if (main != null && eq != null)
            {
                // Check whether we have (and can load) a cached version of the combined filter
                string tempString = theEQImpulseName + "_" + theImpulsePath;
                string filterName = "CC" + tempString.GetHashCode().ToString("x10").ToUpperInvariant();
                string filterFile = Path.Combine(_tempFolder, filterName + ".filter");
                if (_debug)
                {
                    Trace.WriteLine(filterName);
                }
                if (File.Exists(filterFile))
                {
                    try
                    {
                        // Just read the cached EQ filter from disk
                        combinedFilter = new WaveReader(filterFile);
                    }
                    catch (Exception e)
                    {
                        if (_debug)
                        {
                            Trace.WriteLine("LoadImpulse1: " + e.Message);
                        }
                    }
                }

                if (combinedFilter == null)
                {
                    // Convolve the room-correction impulse with the EQ impulse to make just one.
                    // (this is quite slow)
                    FastConvolver temp = new FastConvolver();
                    temp.partitions = 0;
                    temp.impulse = eq;
                    temp.Input = main;
                    combinedFilter = temp;

                    try
                    {
                        // Write the combined impulse
                        temp.Reset();
                        WaveWriter tempWriter = new WaveWriter(filterFile);
                        tempWriter.Format = WaveFormat.IEEE_FLOAT;
                        tempWriter.BitsPerSample = 64;
                        tempWriter.Input = temp;
                        tempWriter.Run();
                        tempWriter.Close();

                        if (_debug)
                        {
                            // DEBUG: Write the combined impulse as WAV16
                            temp.Reset();
                            tempWriter = new WaveWriter(filterFile + ".wav");
                            tempWriter.Format = WaveFormat.PCM;
                            tempWriter.BitsPerSample = 16;
                            tempWriter.Gain = 0.1;
                            tempWriter.Dither = DitherType.NONE;//.TRIANGULAR;
                            tempWriter.Input = temp;
                            tempWriter.Run();
                            tempWriter.Close();
                        }
                    }
                    catch (Exception e)
                    {
                        if (_debug)
                        {
                            Trace.WriteLine("LoadImpulse2: " + e.Message);
                        }
                    }
                }
            }

            _MainConvolver.impulse = combinedFilter;

            if (combinedFilter != null)
            {
                // Calculate loudness-adjusted volume of each channel of the impulse
                _impulseVolumes.Clear();
                for (ushort j = 0; j < combinedFilter.NumChannels; j++)
                {
                    double v = Loudness.WeightedVolume(combinedFilter.Channel(j));
                    _impulseVolumes.Add(v);
                    if (_debug)
                    {
                        Trace.WriteLine("WV{0}: {1}", j, v);
                    }
                }
            }
            combinedFilter = null;

            if (_debug)
            {
                TimeSpan ts = DateTime.Now.Subtract(dtStart);
                Trace.WriteLine("Loadmpulse " + ts.TotalMilliseconds);
            }
        }

        static SoundObj GetMainImpulse(out string actualPath)
        {
            DateTime dtStart = DateTime.Now;
            if (_impulsePath == "") _impulsePath = null;
            if (_impulsePath == "-") _impulsePath = null;
            if (_matrixFilter == "") _matrixFilter = null;
            if (_matrixFilter == "-") _matrixFilter = null;
            if (_bformatFilter == "") _bformatFilter = null;
            if (_bformatFilter == "-") _bformatFilter = null;
            Trace.WriteLine("Impulse {0}, matrix {1}", CleanPath(_dataFolder, _impulsePath), CleanPath(_dataFolder, _matrixFilter));

            // note: we window the room correction impulse if it's too long
            WaveReader impulseReader = null;
            SoundObj impulseObj = null;
            actualPath = null;

            if (!String.IsNullOrEmpty(_impulsePath))
            {
                impulseReader = GetAppropriateImpulseReader(_impulsePath, out actualPath);
            }
            if (impulseReader != null)
            {
                if (impulseReader.Iterations > _maxImpulseLength)
                {
                    // This impulse is too long.
                    // Trim it to length.
                    int hwid = _maxImpulseLength / 2;
                    int qwid = _maxImpulseLength / 4;
                    SoundBuffer buff = new SoundBuffer(impulseReader);
                    buff.ReadAll();
                    int center = buff.MaxPos();
                    BlackmanHarris wind;
                    int startpos;
                    if (center < hwid)
                    {
                        wind = new BlackmanHarris(center, qwid, qwid);
                        startpos = 0;
                    }
                    else
                    {
                        wind = new BlackmanHarris(hwid, qwid, qwid);
                        startpos = center - hwid;
                    }
                    //                        int startpos = center < hwid ? 0 : (center - hwid);
                    wind.Input = buff.Subset(startpos, _maxImpulseLength);
                    impulseObj = wind;
                }
                else
                {
                    impulseObj = impulseReader;
                }
            }

            if (_debug)
            {
                TimeSpan ts = DateTime.Now.Subtract(dtStart);
                Trace.WriteLine("GetMainImpulse " + ts.TotalMilliseconds);
            }
            return impulseObj;
        }


        static SoundObj GetEQImpulse(ISoundObj mainImpulse, uint sampleRate, out string filterName)
        {
            DateTime dtStart = DateTime.Now;
            SoundObj filterImpulse = null;

            // Construct a string describing the filter
            string filterDescription = "EQ" + _eqBands + "_" + _inputSampleRate + "_" + _eqLoudness + "_" + FlatnessFilterPath(_impulsePath, sampleRate, _eqFlatness);

            bool nothingToDo = (_eqLoudness==0);
            nothingToDo &= (_impulsePath == null) || (_eqFlatness == 100);

            List<string> fgd = new List<string>();
            foreach (FreqGain fg in _eqValues)
            {
                fgd.Add(fg.Freq + "@" + fg.Gain);
                nothingToDo &= (fg.Gain == 0);
            }
            filterDescription = filterDescription + String.Join("_", fgd.ToArray());
            filterDescription = filterDescription + "_IM_" + _impulsePath;

            // Cached filters are named by hash of this string
            filterName = "EQ" + filterDescription.GetHashCode().ToString("x10").ToUpperInvariant();
            if (nothingToDo)
            {
                Trace.WriteLine("EQ flat");
                WriteJSON(_eqValues, null, null);
                return null;
            }
            else
            {
                Trace.WriteLine(filterName);
            }
            string filterFile = Path.Combine(_tempFolder, filterName + ".filter");

            // Does the cached filter exist?
            if (File.Exists(filterFile))
            {
                try
                {
                    // Just read the cached EQ filter from disk
                    filterImpulse = new WaveReader(filterFile);
                }
                catch (Exception e)
                {
                    if (_debug)
                    {
                        Trace.WriteLine("GetEQImpulse1: " + e.Message);
                    }
                }
            }
            if(filterImpulse==null)
            {
                // Construct a filter impulse from the list of EQ values
                SoundObj eqFilter = new FilterImpulse(0, _eqValues, FilterInterpolation.COSINE, _inputSampleRate);
                filterImpulse = eqFilter;

                ISoundObj qtFilter = GetQuietnessFilter(_inputSampleRate, _eqLoudness);
                if (qtFilter != null)
                {
                    // Convolve the two, to create a EQ-and-loudness filter
                    FastConvolver tmpConvolver = new FastConvolver();
                    tmpConvolver.partitions = 0;
                    tmpConvolver.impulse = qtFilter;
                    tmpConvolver.Input = eqFilter;
                    filterImpulse = tmpConvolver;
                }

                ISoundObj ftFilter = GetFlatnessFilter(_impulsePath, mainImpulse, _eqFlatness);
                if (ftFilter != null)
                {
                    // Convolve the two, to create a EQ-and-loudness filter
                    FastConvolver tmpConvolver2 = new FastConvolver();
                    tmpConvolver2.partitions = 0;
                    tmpConvolver2.impulse = filterImpulse;
                    tmpConvolver2.Input = ftFilter;
                    filterImpulse = tmpConvolver2;
                }

                // Blackman window to make the filter smaller?

                try
                {
                    // Write the filter impulse to disk
                    WaveWriter wri = new WaveWriter(filterFile);
                    wri.Input = filterImpulse;
                    wri.Format = WaveFormat.IEEE_FLOAT;
                    wri.BitsPerSample = 64;
                    wri.Run();
                    wri.Close();

                    if (_debug)
                    {
                        // DEBUG: Write the filter impulse as wav16
                        wri = new WaveWriter(filterFile + ".wav");
                        wri.Input = filterImpulse;
                        wri.Format = WaveFormat.PCM;
                        wri.BitsPerSample = 16;
                        wri.Normalization = -1.0;
                        wri.Dither = DitherType.NONE;//.TRIANGULAR;
                        wri.Run();
                        wri.Close();
                    }

                    // Write a JSON description of the filter
                    WriteJSON(_eqValues, filterImpulse, filterName);
                }
                catch (Exception e)
                {
                    if (_debug)
                    {
                        Trace.WriteLine("GetEQImpulse2: " + e.Message);
                    }
                }
            }
            filterImpulse.Reset();
            if (_debug)
            {
                TimeSpan ts = DateTime.Now.Subtract(dtStart);
                Trace.WriteLine("GetEQImpulse " + ts.TotalMilliseconds);
            }

            // Copy the filter's JSON description (if available) into "current.json"
            CopyJSON(filterName);

            return filterImpulse;
        }


        static void LoadMatrixFilter()
        {
            SoundObj im = GetMatrixImpulse();
            if (im != null)
            {
                _MatrixConvolver.partitions = 0;
                _depthSkewer.Input = im;
                _depthSkewer.Skew = _depth;
                _MatrixConvolver.impulse = _depthSkewer;
            }
        }

        static SoundObj GetMatrixImpulse()
        {
            // Load the stereo-matrix filter, if there is one

            // The matrix impulse needs to be shuffled before we use it
            Shuffler filterShuffler = new Shuffler();

            if (!String.IsNullOrEmpty(_matrixFilter))
            {
                string ignore;
                WaveReader matrixReader = GetAppropriateImpulseReader(_matrixFilter, out ignore);
                if (matrixReader != null)
                {
                    filterShuffler.Input = matrixReader;

                    // Calculate loudness-adjusted volume of the matrix impulse
                    double matrixVolume = Loudness.WeightedVolume(filterShuffler);
                    if (Math.Abs(1 - matrixVolume) > 0.001)
                    {
                        // Adjust the main gain by this
                        for (int j = 0; j < _impulseVolumes.Count; j++)
                        {
                            _impulseVolumes[j] *= matrixVolume;
                        }
                    }
                }
            }

            return filterShuffler;
        }


        #region JSON

        /// <summary>
        /// Copy the named filter's JSON description to "current"
        /// </summary>
        /// <param name="filterName">base filename, no extension</param>
        private static void CopyJSON(string filterName)
        {
            if (_debug)
            {
                Trace.WriteLine("CopyJSON {0}", filterName);
            }
            string jsonFile = Path.Combine(_tempFolder, filterName + ".json");
            string currFile = Path.Combine(_tempFolder, _userID.Replace(':', '_') + ".current.json");
            if (File.Exists(jsonFile))
            {
                try
                {
                    File.Copy(jsonFile, currFile, true);
                }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// Write the JSON description of the given filter impulse
        /// </summary>
        /// <param name="filterImpulse">ISoundObj</param>
        /// <param name="filterName">base filename, no extension</param>
        private static void WriteJSON(FilterProfile eqValues, ISoundObj filterImpulse, string filterName)
        {
            FilterProfile lfg;
            if (filterImpulse == null && filterName == null)
            {
                // If both parameters are null,
                // write a null "current.json"
                filterName = _userID.Replace(':', '_') + ".current";
                lfg = new FilterProfile();
                lfg.Add(new FreqGain(20, 0));
                lfg.Add(new FreqGain(22050, 0));
                if (_debug)
                {
                    Trace.WriteLine("WriteJSON {0} (null)", filterName);
                }
            }
            else
            {
                if (_debug)
                {
                    Trace.WriteLine("WriteJSON {0}", filterName);
                }

                // FFT the impulse
                // Smooth its magnitude response by ERB bands
                // Write the result to filterName.json
                lfg = new FilterProfile(filterImpulse, 0.5);
            }

            // Write to JSON
            string jsonFile = Path.Combine(_tempFolder, filterName + ".json");
            FileStream fs = new FileStream(jsonFile, FileMode.Create);
            StreamWriter sw = new StreamWriter(fs);
            sw.WriteLine("{");
            sw.Write(eqValues.ToJSONString("EQ", "User-set equalizer values") + ",");
            sw.Write(lfg.ToJSONString("Points", "Combined loudness, flatness and EQ filter"));
            sw.WriteLine("}");
            sw.Close();
            fs.Close();
        }
        #endregion


        #region Ambisonics
        static void LoadBFormatFilter()
        {
            // NOP
            // Can't yet change BFormat filter during processing
        }

        static ISoundObj DecodeBFormat(ISoundObj source)
        {
            if (String.IsNullOrEmpty(_ambiType) || _ambiType=="-")
            {
                _ambiType = "UHJ";
            }

            if (_ambiRotateX != 0 || _ambiRotateY != 0 || _ambiRotateZ != 0)
            {
                // Rotate the soundfield before decoding
                source = RotateBFormat(source);
            }

            switch (_ambiType.ToUpperInvariant())
            {
                case "CROSSED":
                    return DecodeBFormatCrossed(source);

                case "BLUMLEIN":
                    return DecodeBFormatBlumlein(source);

                case "BINAURAL":
                    return DecodeBFormatBinaural(source);

                case "MATRIX":
                    return DecodeBFormatMatrix(source);

                case "UHJ":
                default:
                    return DecodeBFormatUHJ(source);
            }
            //throw new NotImplementedException();
        }


        static ISoundObj RotateBFormat(ISoundObj source)
        {
            // Rotate a B-Format source
            ISoundObj channelW = new SingleChannel(source, 0);
            ISoundObj channelX = new SingleChannel(source, 1, true);
            ISoundObj channelY = new SingleChannel(source, 2, true);
            ISoundObj channelZ = new SingleChannel(source, 3, true);

            double rx = MathUtil.Radians(_ambiRotateX);
            double ry = MathUtil.Radians(_ambiRotateY);
            double rz = MathUtil.Radians(_ambiRotateZ);

            // Mixer W = new Mixer();
            Mixer X = new Mixer();
            Mixer Y = new Mixer();
            Mixer Z = new Mixer();

            // W is unchanged (omni)
            // W.Add(channelW, 1.0);

            // http://www.muse.demon.co.uk/fmhrotat.html

            // tilt
            // x1 = x * 1       + y * 0       + z * 0
            // y1 = x * 0       + y * Cos(rx) - z * Sin(rx)
            // z1 = x * 0       + y * Sin(rx) + z * Cos(rx)
            // tumble
            // x2 = x * Cos(ry) + y * 0       - z * Sin(ry)
            // y2 = x * 0       + y * 1       + z * 0
            // z2 = x * Sin(ry) + y * 0       + z * Cos(ry)
            // rotate
            // x3 = x * Cos(rz) - y * Sin(rz) + z * 0
            // y3 = x * Sin(rz) + y * Cos(rz) + z * 0
            // z3 = x * 0       + y * 0       + z * 1
            // (read that downwards to get:)

            X.Add(channelX, Math.Cos(ry) * Math.Cos(rz));
            X.Add(channelY, -Math.Sin(rz));
            X.Add(channelZ, -Math.Sin(ry));

            Y.Add(channelX, Math.Sin(rz));
            Y.Add(channelY, Math.Cos(rx) * Math.Cos(rz));
            Y.Add(channelZ, -Math.Sin(rx));

            Z.Add(channelX, Math.Sin(ry));
            Z.Add(channelY, Math.Sin(rz));
            Z.Add(channelZ, Math.Cos(rz) * Math.Cos(ry));

            ChannelSplicer ret = new ChannelSplicer();
            ret.Add(channelW);
            ret.Add(X);
            ret.Add(Y);
            ret.Add(Z);

            return ret;
        }


        static ISoundObj DecodeBFormatMatrix(ISoundObj source)
        {
            // _ambiMatrix is the matrix file
            throw new NotImplementedException();
        }


        static ISoundObj DecodeBFormatCrossed(ISoundObj source)
        {
            // Stereo decode:
            // A shuffle of the X (front-back) and Y (left-right) channels
            // with some W mixed, so the effective mics become (hyper)cardioid instead of figure-8.
            // If _ambiCardioid=0, this is the same as Blumlein.
            // If _ambiCardioid=1, this is cardioid
            // Of any value between, for hypercardioid patterns.

            // Separate the WXY channels
            ISoundObj channelW = new SingleChannel(source, 0);
            ISoundObj channelX = new SingleChannel(source, 1, true);
            ISoundObj channelY = new SingleChannel(source, 2, true);

            // The _ambiMicAngle says angle between the virtual microphones (degrees)
            // e.g. if this is 100
            //   Left and right are each 50 degrees from forward.
            // These are implemented by mixing appropriate amounts of X and Y;
            //   X * cos(angle/2)
            //   Y * sin(angle/2)
            //
            // For default mic angle of 90 degrees, mulX=mulY=sqrt(2)/2
            //
            double mulX = Math.Cos(MathUtil.Radians(_ambiMicAngle / 2));
            double mulY = Math.Sin(MathUtil.Radians(_ambiMicAngle / 2));

            // Mix back together, adding appropriate amounts of W.
            // The W channel gain is conventionally sqrt(2)/2 relative to X and Y,
            Mixer mixerL = new Mixer();
            mixerL.Add(channelW, _ambiCardioid * MathUtil.INVSQRT2);
            mixerL.Add(channelX, mulX);
            mixerL.Add(channelY, mulY);

            Mixer mixerR = new Mixer();
            mixerR.Add(channelW, _ambiCardioid * MathUtil.INVSQRT2);
            mixerR.Add(channelX, mulX);
            mixerR.Add(channelY, -mulY);

            // output in stereo
            ChannelSplicer stereo = new ChannelSplicer();
            stereo.Add(mixerL);
            stereo.Add(mixerR);
            return stereo;
        }

        static ISoundObj DecodeBFormatBlumlein(ISoundObj source)
        {
            // Pure fig-8 Blumlein decode is simply a shuffle of the X (front-back) and Y (left-right) channels
            // W and Z are ignored.
            // No shelf filtering or distance compensation.

            TwoChannel xy = new TwoChannel(source, 1, 2);
            Shuffler blum = new Shuffler();
            blum.DeltaGain = 1.0;
            blum.SigmaGain = 1.0;
            blum.Input = xy;
            return blum;
        }

        static ISoundObj DecodeBFormatBinaural(ISoundObj source)
        {
            throw new NotImplementedException();

            ISoundObj input = source;
            uint sr = input.SampleRate;

            // Convolve the BFormat data with the matrix filter
            if (!String.IsNullOrEmpty(_bformatFilter))
            {
                string ignore;
                WaveReader rdr = GetAppropriateImpulseReader(_bformatFilter, out ignore);
                FastConvolver ambiConvolver = new FastConvolver(source, rdr);
                input = ambiConvolver;
            }

            // Cardioid directed at four (or six) virtual loudspeakers
            IEnumerator<ISample> src = input.Samples;
            CallbackSource bin = new CallbackSource(2, sr, delegate(long j)
            {
                if (src.MoveNext())
                {
                    ISample s = src.Current;
                    double w = s[0];
                    double x = s[1];
                    double y = s[2];
                    double z = s[3];
                    double wFactor = -0.5;
                    double left = x + y + z + (wFactor * w);
                    double right = x - y + z + (wFactor * w);
                    ISample sample = new Sample2(left, right);
                    return sample;
                }
                return null;
            });

            return bin;
        }


        static ISoundObj DecodeBFormatUHJ(ISoundObj source)
        {
            ISoundObj input = source;
            uint sr = input.SampleRate;
            /*
            if (_ambiUseShelf)
            {
                // Shelf-filters
                // boost W at high frequencies, and boost X, Y at low frequencies
                FilterProfile lfgXY = new FilterProfile();
                lfgXY.Add(new FreqGain(_ambiShelfFreq / 2, 0));
                lfgXY.Add(new FreqGain(_ambiShelfFreq * 2, -1.25));
                FilterImpulse fiXY = new FilterImpulse(0, lfgXY, FilterInterpolation.COSINE, sr);

                FilterProfile lfgW = new FilterProfile();
                lfgW.Add(new FreqGain(_ambiShelfFreq / 2, 0));
                lfgW.Add(new FreqGain(_ambiShelfFreq * 2, 1.76));
                FilterImpulse fiW = new FilterImpulse(0, lfgW, FilterInterpolation.COSINE, sr);
            }
            if (_ambiUseDistance)
            {
                // Distance compensation filters
                // apply phase shift to X, Y at (very) low frequencies
                double fc = MathUtil.FcFromMetres(_ambiDistance);
                IIR1 discomp = new IIR1LP(sr, fc, 8192);    // tbd: chain this
            }
            */

            // Transformation filters
            //
            // Primary reference:
            // Gerzon 1985 "Ambisonics in Multichannel Broadcasting and Video"
            //
            // Coefficients from: http://en.wikipedia.org/wiki/Ambisonic_UHJ_format:
            // S = 0.9396926*W + 0.1855740*X
            // D = j(-0.3420201*W + 0.5098604*X) + 0.6554516*Y
            // Left = (S + D)/2.0
            // Right = (S - D)/2.0
            // which makes
            // Left = (0.092787 + 0.2549302j)X + (0.4698463 - 0.17101005j)W + (0.3277258)Y
            // Right= (0.092787 - 0.2549302j)X + (0.4698463 + 0.17101005j)W - (0.3277258)Y
            //
            // Coefficients from: http://www.york.ac.uk/inst/mustech/3d_audio/ambis2.htm
            // Left = (0.0928 + 0.255j)X + (0.4699 - 0.171j)W + (0.3277)Y
            // Right= (0.0928 - 0.255j)X + (0.4699 + 0.171j)W - (0.3277)Y

            // The Mid-Side versions are simpler
            // L+R = (0.0928 + 0.255j)X + (0.4699 - 0.171j)W + (0.3277)Y + ((0.0928 - 0.255j)X + (0.4699 + 0.171j)W - (0.3277)Y)
            //     = (0.1856)X          + (0.9398)W
            // L-R = (0.0928 + 0.255j)X + (0.4699 - 0.171j)W + (0.3277)Y - ((0.0928 - 0.255j)X + (0.4699 + 0.171j)W - (0.3277)Y)
            //     =          (0.510j)X +          (0.342j)W + (0.6554)Y
            // but since we're delaying signal via convolution anyway, not *too* much extra processing to do in LR mode...

            // Separate the WXY channels
            ISoundObj channelW = new SingleChannel(input, 0);
            ISoundObj channelX = new SingleChannel(input, 1, true);
            ISoundObj channelY = new SingleChannel(input, 2, true);

            // Z not used; height is discarded in UHJ conversion.
            // Don't assume it's there; horizontal-only .AMB files won't have a fourth channel
//          ISoundObj channelZ = new SingleChannel(input, 3);

            // Phase shift j is implemented with Hilbert transforms
            // so let's load up some filters, multiply by the appropriate coefficients.
            int len = 8191;
            PhaseMultiplier xl = new PhaseMultiplier(new Complex(0.0927870, 0.25493020), len, sr);
            PhaseMultiplier wl = new PhaseMultiplier(new Complex(0.4698463, -0.17101005), len, sr);
            PhaseMultiplier yl = new PhaseMultiplier(new Complex(0.3277258, 0.00000000), len, sr);
            PhaseMultiplier xr = new PhaseMultiplier(new Complex(0.0927870, -0.25493020), len, sr);
            PhaseMultiplier wr = new PhaseMultiplier(new Complex(0.4698463, 0.17101005), len, sr);
            PhaseMultiplier yr = new PhaseMultiplier(new Complex(-0.3277258, 0.00000000), len, sr);

            // The convolvers to filter
            FastConvolver cwl = new FastConvolver(channelW, wl);
            FastConvolver cxl = new FastConvolver(channelX, xl);
            FastConvolver cyl = new FastConvolver(channelY, yl);
            FastConvolver cwr = new FastConvolver(channelW, wr);
            FastConvolver cxr = new FastConvolver(channelX, xr);
            FastConvolver cyr = new FastConvolver(channelY, yr);

            // Sum to get the final output of these things:
            Mixer mixerL = new Mixer();
            mixerL.Add(cwl, 1.0);
            mixerL.Add(cxl, 1.0);
            mixerL.Add(cyl, 1.0);

            Mixer mixerR = new Mixer();
            mixerR.Add(cwr, 1.0);
            mixerR.Add(cxr, 1.0);
            mixerR.Add(cyr, 1.0);

            // output in stereo
            ChannelSplicer uhj = new ChannelSplicer();
            uhj.Add(mixerL);
            uhj.Add(mixerR);

            return uhj;
        }
        #endregion


        #region Quietness
        private static ISoundObj GetQuietnessFilter(uint nSampleRate, double nQuietness)
        {
            if (nQuietness == 0)
            {
                return null;
            }
            // Construct an impulse for the quietness setting.
            // nQuietness is value from 0 to 100 (percentage);
            // map this to phon values from 20 to 90 (approx)
            DateTime dtStart = DateTime.Now;
            FilterProfile spl = Loudness.DifferentialSPL(20, 20 + (nQuietness / 2));    // sound
            ISoundObj filterImpulse = new FilterImpulse(4096, spl, FilterInterpolation.COSINE, nSampleRate);
            if (_debug)
            {
                TimeSpan ts = DateTime.Now.Subtract(dtStart);
                Trace.WriteLine("GetQuietnessFilter1  " + ts.TotalMilliseconds);

                // DEBUG: Write the quietness impulse as wav16
                string sPath = Path.Combine(_tempFolder, "QT_" + nQuietness);
                WaveWriter wri = new WaveWriter(sPath + ".wav");
                wri.Input = filterImpulse;
                wri.Format = WaveFormat.PCM;
                wri.BitsPerSample = 16;
                wri.Normalization = -1.0;
                wri.Dither = DitherType.NONE;//.TRIANGULAR;
                wri.Run();
                wri.Close();

                ts = DateTime.Now.Subtract(dtStart);
                Trace.WriteLine("GetQuietnessFilter " + ts.TotalMilliseconds);
            }
            return filterImpulse;
        }
        #endregion


        #region Flatness
        private static string FlatnessFilterName(string impulsePath, uint sampleRate, double nFlatness)
        {
            // Flatness filters are cached in the _tempFolder
            // named by "FL" + the hash of the filter's path + sampleRate + ("_0" thru "_100")
            // where 0 means "not at all flat": filter is the full inverse of the impulse;
            // up to 100 means "completely flat": filter is the dirac, and doesn't affect the impulse at all.
            string hashKey = "";
            if (impulsePath != null)
            {
                string s = impulsePath;
                s += File.GetLastWriteTimeUtc(impulsePath).Ticks.ToString();
                hashKey = s.GetHashCode().ToString("x10").ToUpperInvariant();
            }
            string filterNameBase = "FL" + hashKey + "_" + sampleRate;
            return filterNameBase + "_" + nFlatness;
        }

        private static string FlatnessFilterPath(string impulsePath, uint sampleRate, double nFlatness)
        {
            string filterFileBase = Path.Combine(_tempFolder, FlatnessFilterName(impulsePath, sampleRate, nFlatness));
            string filterFile = filterFileBase + ".filter";
            return filterFile;
        }

        private static ISoundObj GetFlatnessFilter(string impulsePath, ISoundObj impulseObj, double nFlatness)
        {
            ISoundObj filter = null;
            if (impulseObj != null && impulsePath != null && nFlatness!=100)
            {
                uint sr = impulseObj.SampleRate;
                if (_debug)
                {
                    Trace.WriteLine(FlatnessFilterName(impulsePath, sr, nFlatness));
                }
                // Do we already have a cached flatness-filter?
                // (Building these things is expensive...)
                string sFile = FlatnessFilterPath(impulsePath, sr, nFlatness);
                if (File.Exists(sFile))
                {
                    try
                    {
                        filter = new WaveReader(sFile);
                    }
                    catch (Exception e)
                    {
                        if (_debug)
                        {
                            Trace.WriteLine("GetFlatnessFilter: " + e.Message);
                        }
                    }
                }
                if (filter == null)
                {
                    // Generate a new flatness filter then
                    return GenerateFlatnessFilter(impulsePath, impulseObj, nFlatness);
                }
            }
            return filter;
        }


        private static ISoundObj GenerateFlatnessFilter(string impulsePath, ISoundObj impulseObj, double nFlatness)
        {
            // Flatness-filters are single channel
            // nFlatness is from 0 to 100
            // 0: follow the contours of the impulse
            // 100: completely flat

            DateTime dtStart = DateTime.Now;
            ISoundObj filterImpulse = null;
            uint nSR = impulseObj.SampleRate;
            uint nSR2 = nSR / 2;
            string sPath = FlatnessFilterPath(impulsePath, nSR, nFlatness);

            // Low flatness values (to 0) => very un-smooth
            // High flatness values (to 100) => very smooth
            double detail = (nFlatness / 50) + 0.05;

            // Get profile of the impulse
            FilterProfile lfg = new FilterProfile(impulseObj, detail);

            // Scale by the flatness values
            lfg = lfg * ((100 - nFlatness) / 100);

            // Invert
            lfg = lfg.Inverse(20);

            // Zero at HF
            lfg.Add(new FreqGain(nSR2 - 100, 0));

            // Build the flatness filter
            filterImpulse = new FilterImpulse(8192, lfg, FilterInterpolation.COSINE, nSR);

            try
            {
                // Write the filter impulse to disk
                WaveWriter wri = new WaveWriter(sPath);
                wri.Input = filterImpulse;
                wri.Format = WaveFormat.IEEE_FLOAT;
                wri.BitsPerSample = 64;
                wri.Run();
                wri.Close();

                if (_debug)
                {
                    // DEBUG: Write the flatness impulse as wav16
                    wri = new WaveWriter(sPath + ".wav");
                    wri.Input = filterImpulse;
                    wri.Format = WaveFormat.PCM;
                    wri.BitsPerSample = 16;
                    wri.Normalization = -1.0;
                    wri.Dither = DitherType.NONE;//.TRIANGULAR;
                    wri.Run();
                    wri.Close();
                }
            }
            catch (Exception e)
            {
                if (_debug)
                {
                    Trace.WriteLine("GenerateFlatnessFilter: " + e.Message);
                }
            }

            impulseObj.Reset();
            if (_debug)
            {
                TimeSpan ts = DateTime.Now.Subtract(dtStart);
                Trace.WriteLine("GenerateFlatnessFilter " + ts.TotalMilliseconds);
            }
            return filterImpulse;
        }
        #endregion

        static string GetResampledImpulsePath(string filePath)
        {
            // For a file
            //   /<inguzeq>/Impulses/Something.wav
            // the 96kHz resampled version should be called
            //   /<inguzeq>/Temp/96000_<hash>_Impulses_Something.wav
            // where <hash> is the timestamp of the original file, if it exists
            // Note: sox uses the file extension to know it should write a WAV file, so we stick the sample rate at the start
            string newName = CleanPath(_dataFolder, filePath);
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
            {
                newName = newName.Replace(c, '_');
            }
            string prefix = _inputSampleRate + "_";
            if (File.Exists(filePath))
            {
                prefix = prefix + File.GetLastWriteTimeUtc(filePath).GetHashCode().ToString("x10").ToUpperInvariant() + "_";
            }
            newName = prefix + newName;
            return Path.Combine(_tempFolder, newName);
            // Resampled file should always have WAV extension (otherwise sox gets confused)
        }

        static WaveReader GetAppropriateImpulseReader(string filePath, out string actualPath)
        {
            return TryGetAppropriateImpulseReader(filePath, true, out actualPath);
        }

        static WaveReader TryGetAppropriateImpulseReader(string filePath, bool allowResample, out string actualPath)
        {
            WaveReader rdr = null;
            bool isExtensibleFormat = false;
            WaveFormat format = WaveFormat.ANY;
            bool needResample = false;
            string resamplePath = GetResampledImpulsePath(filePath);
            actualPath = null;

            // Check the impulse file.
            if (File.Exists(filePath))
            {
                try
                {
                    rdr = new WaveReader(filePath);
                    actualPath = filePath;
                    if (rdr.SampleRate != 0 && rdr.SampleRate != _inputSampleRate)
                    {
                        Trace.WriteLine("Can't use {0}: its sample rate is {1} not {2}", CleanPath(_dataFolder, filePath), rdr.SampleRate, _inputSampleRate);
                        isExtensibleFormat = (rdr.FormatEx != null);
                        format = rdr.Format;
                        rdr.Close();
                        rdr = null;
                        actualPath = null;
                        needResample = true;
                    }
                }
                catch (Exception e)
                {
                    Trace.WriteLine("Can't use {0}: {1}", CleanPath(_dataFolder, filePath), e.Message);
                }
            }
            else
            {
                Trace.WriteLine("Can't use {0}: not found", CleanPath(_dataFolder, filePath));
            }

            if (rdr == null)
            {
                // No luck there.  Check for an already-resampled version.
                if (File.Exists(resamplePath))
                {
                    try
                    {
                        rdr = new WaveReader(resamplePath);
                        if (rdr.SampleRate != 0 && rdr.SampleRate != _inputSampleRate)
                        {
                            // Oh dear! This shouldn't happen; after all, the resampled file's name is designed to match sample rates
                            Trace.WriteLine("Can't use {0}: its sample rate is {1} not {2}", CleanPath(_dataFolder, resamplePath), rdr.SampleRate, _inputSampleRate);
                            rdr.Close();
                            rdr = null;
                        }
                        else
                        {
                            actualPath = resamplePath;
                            Trace.WriteLine("Using resampled version {0} instead", CleanPath(_dataFolder, resamplePath));
                            needResample = false;
                        }
                    }
                    catch (Exception e)
                    {
                        Trace.WriteLine("Can't use {0}: {1}", CleanPath(_dataFolder, resamplePath), e.Message);
                    }
                }
                else if(allowResample)
                {
                    Trace.WriteLine("Can't use {0}: not found", CleanPath(_dataFolder, resamplePath));
                }
            }

            if (needResample && allowResample)
            {
                // If 'sox' is available,
                // use it to resample the filter we're trying to use
                //  sox <input.wav> -r <new_sample_rate> <output.wav> polyphase
                //
                // The sox application might be either
                // - in the same folder as this application (quite likely...), or
                // - somewhere else on the path
                // and it might be called "sox" or "sox.exe", depending on the operating system (of course).
                //

                if (true || isExtensibleFormat)
                {
                    // sox can't handle the WaveFormatExtensible file type, so we need to save a clean copy of the file first
                    // ALSO: sox goes very wrong if the original is near 0dBfs
                    // so we normalize ALWAYS before resample.
                    rdr = new WaveReader(filePath);
                    string newFile = "RE" + filePath.GetHashCode().ToString("x10").ToUpperInvariant() + ".wav";
                    string newPath = Path.Combine(_tempFolder, newFile);
                    WaveWriter tempWriter = new WaveWriter(newPath);
                    tempWriter.Input = rdr;
                    tempWriter.Format = rdr.Format;
                    tempWriter.BitsPerSample = rdr.BitsPerSample;
                    tempWriter.Normalization = -6;
                    tempWriter.Run();
                    tempWriter.Close();
                    rdr.Close();
                    rdr = null;
                    filePath = newPath;
                }

                // _soxExe = "sox";
                string exeName = _soxExe;
                if(File.Exists(Path.Combine(_pluginFolder, _soxExe + ".exe")))
                {
                    exeName = "\"" + Path.Combine(_pluginFolder, _soxExe + ".exe") + "\"";
                }
                else if (File.Exists(Path.Combine(_pluginFolder, _soxExe)))
                {
                    exeName = Path.Combine(_pluginFolder, _soxExe);
                }

                // _soxFmt = "\"{0}\" -r {1} \"{2}\" polyphase";
                string soxArgs = String.Format(_soxFmt, filePath, _inputSampleRate, resamplePath);

                Trace.WriteLine("Trying {0} {1}", exeName, soxArgs);

                System.Diagnostics.Process soxProcess = new System.Diagnostics.Process();
                System.Diagnostics.ProcessStartInfo soxInfo = new System.Diagnostics.ProcessStartInfo();
                soxInfo.Arguments = soxArgs;
                soxInfo.FileName = exeName;
                soxInfo.UseShellExecute = false;
                soxInfo.RedirectStandardError = true;

                soxProcess.StartInfo = soxInfo;
                try
                {
                    soxProcess.Start();

                    // Wait for the sox process to finish!
                    string err = soxProcess.StandardError.ReadToEnd();
                    soxProcess.WaitForExit(500);
                    if (soxProcess.HasExited)
                    {
                        int n = soxProcess.ExitCode;
                        if (n != 0)
                        {
                            Trace.WriteLine("No, that didn't seem to work: {0}", err);
                        }
                        else
                        {
                            Trace.WriteLine("Yes, that seemed to work.");
                            rdr = TryGetAppropriateImpulseReader(resamplePath, false, out actualPath);
                        }
                    }
                }
                catch (Exception e)
                {
                    Trace.WriteLine("That didn't seem to work: {0}", e.Message);
                }
            }
            if (rdr == null)
            {
                Trace.WriteLine("No suitable impulse found ({0}).", filePath);
            }
            return rdr;
        }


        private static void CleanUp()
        {
            try
            {
                // If the 'temp' folder doesn't contain a file
                // 'version_x_x_x.txt" matching our version number,
                // then the cache files in that folder are from a previous version and should all go away.
                string verFile = "version_" + DSPUtil.DSPUtil.VERSION.ToString().Replace('.', '_') + ".txt";
                string verPath = Path.Combine(_tempFolder, verFile);
                if (!File.Exists(verPath))
                {
                    foreach(string s in Directory.GetFiles(_tempFolder))
                    {
                        File.Delete(s);
                    }
                    File.WriteAllText(verPath, new DateTime().ToString());
                }
            }
            catch (Exception e)
            {
                Trace.WriteLine("Error in CleanUp: " + e.Message);
            }
        }

        private static void Show(string hdr, string msg, int secs)
        {
            ThreadPool.QueueUserWorkItem(delegate(object o)
            {
                SlimCLI server = new SlimCLI();
                SlimPlayer player = new SlimPlayer(server, _userID);
                try
                {
                    server.Open();
                    player.Show(hdr, msg, secs);
                }
                catch (Exception)
                {
                    // ignore, we're done
                }
                finally
                {
                    server.Close();
                }
            });
        }
    }

}
