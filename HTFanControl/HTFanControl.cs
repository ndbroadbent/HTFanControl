﻿using HTFanControl.Players;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Timers;

namespace HTFanControl
{
    class HTFanControl
    {
        private string _OS = ConfigHelper.GetOS();
        public string _errorStatus;
        public string _windtrackError;
        public long _currentVideoTime = 0;
        public string _currentVideoFileName;
        public string _windTrackHeader;
        public int _curCmdIndex = -1;
        public int _nextCmdIndex = 0;
        public bool _isPlaying = false;
        public bool _isEnabled = true;
        public double _offset = 0;
        public bool _offsetEnabled = false;

        public readonly string _videoTimecodePath = Path.Combine(Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName), "windtracks");
        public List<Tuple<TimeSpan, string>> _videoTimeCodes;

        private readonly string _lircMappingPath = Path.Combine(Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName), "lircmapping.txt");
        private Dictionary<string, string> _lircMapping;

        private readonly string _settingsPath = Path.Combine(Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName), "HTFanControlSettings.txt");
        public string _mediaPlayerType = "MPC";
        public string _mediaPlayerIP = "127.0.0.1";
        public string _mediaPlayerPort = "13579";
        public string _lircIP = "127.0.0.1";
        public string _lircPort = "8765";
        public string _lircRemote = "EHF10127B";
        public string _PlexToken;
        public string _plexClientName;
        public string _plexClientIP;
        public string _plexClientPort;
        public string _plexClientGUID;
        public string _globalOffsetMS = "2500";
        public string _spinupOffsetMS = "1500";
        public string _spindownOffsetMS = "0";
        public string _irChan1 = "true";
        public string _irChan2 = "false";
        public string _irChan3 = "false";
        public string _irChan4 = "false";

        private IPositionTimer _videoTimer;
        private readonly Timer _syncTimer;
        private IPlayer _mediaPlayer;

        private Socket _lircSocket;

        public HTFanControl()
        {
            LoadSettings();
            SaveSettings();

            LoadLIRCMapping();

            SelectMediaPlayer();

            ConnectToLIRC();
            SetTransmitters();

            _syncTimer = new Timer(SyncTimerTick, null, 0, Timeout.Infinite);
        }

        public void ReInitialize()
        {
            _videoTimer?.Stop();
            _currentVideoFileName = "";
            _windTrackHeader = "";
            _currentVideoTime = 0;
            _videoTimeCodes = null;
            _curCmdIndex = -1;
            _nextCmdIndex = 0;
            _isPlaying = false;

            LoadLIRCMapping();
            SelectMediaPlayer();
        }

        private void LoadSettings()
        {
            try
            {
                string[] settingsFile = File.ReadAllLines(_settingsPath);
                Dictionary<string, string> settings = settingsFile.ToDictionary(x => x.Split('=')[0], x => x.Split('=')[1]);

                if(!settings.TryGetValue("MediaPlayer", out _mediaPlayerType))
                {
                    _mediaPlayerType = "MPC";
                }
                if(!settings.TryGetValue("MediaPlayerIP", out _mediaPlayerIP))
                {
                    _mediaPlayerIP = "127.0.0.1";
                }
                if(!settings.TryGetValue("MediaPlayerPort", out _mediaPlayerPort))
                {
                    _mediaPlayerPort = "13579";
                }
                if(!settings.TryGetValue("LircIP", out _lircIP))
                {
                    _lircIP = "127.0.0.1";
                }
                if(!settings.TryGetValue("LircPort", out _lircPort))
                {
                    _lircPort = "8765";
                }
                if (!settings.TryGetValue("LircRemote", out _lircRemote))
                {
                    _lircRemote = "EHF10127B";
                }

                settings.TryGetValue("PlexToken", out _PlexToken);
                settings.TryGetValue("PlexClientName", out _plexClientName);
                settings.TryGetValue("PlexClientIP", out _plexClientIP);
                settings.TryGetValue("PlexClientPort", out _plexClientPort);
                settings.TryGetValue("PlexClientGUID", out _plexClientGUID);

                if (!settings.TryGetValue("GlobalOffsetMS", out _globalOffsetMS))
                {
                    _globalOffsetMS = "2500";
                }
                if(!settings.TryGetValue("SpinupOffsetMS", out _spinupOffsetMS))
                {
                    _spinupOffsetMS = "1500";
                }
                if (!settings.TryGetValue("SpindownOffsetMS", out _spindownOffsetMS))
                {
                    _spindownOffsetMS = "0";
                }

                if (ConfigHelper.GetOS() != "win")
                {
                    if(!settings.TryGetValue("IRChan1", out _irChan1))
                    {
                        _irChan1 = "true";
                    }
                    if(!settings.TryGetValue("IRChan2", out _irChan2))
                    {
                        _irChan2 = "false";
                    }
                    if(!settings.TryGetValue("IRChan3", out _irChan3))
                    {
                        _irChan3 = "false";
                    }
                    if(!settings.TryGetValue("IRChan4", out _irChan4))
                    {
                        _irChan4 = "false";
                    }
                }
            }
            catch (Exception e)
            {
                _errorStatus = $"({DateTime.Now:h:mm:ss tt}) Failed to load settings.\n\n{e.Message}";
            }
        }

        public void SaveSettings()
        {
            try
            {
                List<string> settings = new List<string>();
                settings.Add("MediaPlayer=" + _mediaPlayerType);
                settings.Add("MediaPlayerIP=" + _mediaPlayerIP);
                settings.Add("MediaPlayerPort=" + _mediaPlayerPort);
                settings.Add("LircIP=" + _lircIP);
                settings.Add("LircPort=" + _lircPort);
                settings.Add("LircRemote=" + _lircRemote);
                settings.Add("PlexToken=" + _PlexToken);
                settings.Add("PlexClientName=" + _plexClientName);
                settings.Add("PlexClientIP=" + _plexClientIP);
                settings.Add("PlexClientPort=" + _plexClientPort);
                settings.Add("PlexClientGUID=" + _plexClientGUID);
                settings.Add("GlobalOffsetMS=" + _globalOffsetMS);
                settings.Add("SpinupOffsetMS=" + _spinupOffsetMS);
                settings.Add("SpindownOffsetMS=" + _spindownOffsetMS);

                if (ConfigHelper.GetOS() != "win")
                {
                    settings.Add("IRChan1=" + _irChan1);
                    settings.Add("IRChan2=" + _irChan2);
                    settings.Add("IRChan3=" + _irChan3);
                    settings.Add("IRChan4=" + _irChan4);
                }

                File.WriteAllLines(_settingsPath, settings);
            }
            catch (Exception e)
            {
                _errorStatus = $"({DateTime.Now:h:mm:ss tt}) Failed to save settings.\n\n{e.Message}";
            }
        }

        private void SelectMediaPlayer()
        {
            switch (_mediaPlayerType)
            {
                case "MPC":
                    _mediaPlayer = new MPCPlayer(_mediaPlayerIP, _mediaPlayerPort);
                    break;
                case "Kodi":
                    _mediaPlayer = new KodiPlayer(_mediaPlayerIP, _mediaPlayerPort);
                    break;
                case "Plex":
                    _mediaPlayer = new PlexPlayer(_mediaPlayerIP, _mediaPlayerPort, _PlexToken, _plexClientIP, _plexClientPort, _plexClientGUID, _plexClientName);
                    break;
                default:
                    _mediaPlayer = new MPCPlayer(_mediaPlayerIP, _mediaPlayerPort);
                    break;
            }
        }

        public void ToggleFan()
        {
            if (_isEnabled)
            {
                _isEnabled = false;

                byte[] cmd = Encoding.ASCII.GetBytes($"SEND_ONCE {_lircRemote} OFF\n");

                Console.WriteLine("Fans Disabled");
                SendToLIRC(cmd);
            }
            else
            {
                Console.WriteLine("Fans Enabled");

                _isEnabled = true;
                byte[] cmd = new byte[0];

                if (_videoTimer != null)
                {
                    try
                    {
                        if (_isPlaying)
                        {
                            cmd = Encoding.ASCII.GetBytes($"SEND_ONCE {_lircRemote} {_videoTimeCodes[_curCmdIndex].Item2}\n");
                            Console.WriteLine($"Sent CMD: {_videoTimeCodes[_curCmdIndex].Item1.ToString("G").Substring(2, 12)},{_videoTimeCodes[_curCmdIndex].Item2}");

                            SendToLIRC(cmd);
                        }
                    }
                    catch { }
                }
            }
        }

        private void LoadLIRCMapping()
        {
            _lircMapping = null;
            if (File.Exists(_lircMappingPath))
            {
                _lircMapping = new Dictionary<string, string>();
                string[] mappingFile = File.ReadAllLines(_lircMappingPath);

                foreach(string s in mappingFile)
                {
                    try
                    {
                        string[] vals = s.Split('=');
                        _lircMapping.Add(vals[1], vals[0]);
                    }
                    catch { }
                }
            }
        }

        private void SyncTimerTick(object o)
        {
            SyncVideo();
        }

        private async void SyncVideo()
        {
            if (_mediaPlayer != null)
            {
                bool success = _mediaPlayer.Update();

                if (success)
                {
                    _isPlaying = _mediaPlayer.IsPlaying;
                    _currentVideoTime = _mediaPlayer.VideoTime;

                    if (_currentVideoFileName != _mediaPlayer.FileName)
                    {
                        _currentVideoFileName = _mediaPlayer.FileName;
                        LoadVideoTimecodes(_mediaPlayer.FileName, _mediaPlayer.FilePath);

                        if (_videoTimer != null)
                        {
                            await _videoTimer.DisposeAsync(_videoTimeCodes == null);
                        }
                        if (_videoTimeCodes != null)
                        {
                            _videoTimer = new PositionTimer<(string, int)>(_videoTimeCodes.Select((v, i) => (v.Item1, (v.Item2, i))), SendCmd, _mediaPlayer.VideoTimeResolution, ("OFF", -1));
                        }
                        else
                        {
                            _videoTimer = null;
                        }
                    }

                    if (_videoTimer != null)
                    {
                        if (_mediaPlayer.IsPlaying)
                        {
                            _videoTimer.Update(TimeSpan.FromMilliseconds(_mediaPlayer.VideoTime));
                        }
                        else
                        {
                            _videoTimer.Stop();
                        }
                    }
                }
                else
                {
                    _errorStatus = _mediaPlayer.ErrorStatus;
                    ReInitialize();
                }
            }

            _syncTimer.Change(1000, Timeout.Infinite);
        }

        private void SendCmd((string cmd, int index) command)
        {
            var (fanCmd, i) = command;
            _curCmdIndex = i;
            if(i == -1)
            {
                fanCmd = "OFF";

                if (_lircMapping != null && _lircMapping.TryGetValue("STOP", out string stopCMD))
                {
                    fanCmd = stopCMD;
                }
            }
            else
            {
                _nextCmdIndex = i + 1;
            }

            try
            {
                Console.WriteLine($"Sent CMD: {_videoTimeCodes[i].Item1.ToString("G").Substring(2, 12)},{fanCmd}");
            }
            catch
            {
                Console.WriteLine($"Sent CMD: {fanCmd}");
            }

            if (_isEnabled)
            {
                string[] cmds = fanCmd.Split(',');

                foreach(string cmd in cmds)
                {
                    if (_lircMapping != null && _lircMapping.TryGetValue(cmd, out string remote))
                    {
                        _lircRemote = remote;
                    }

                    byte[] cmdBytes = Encoding.ASCII.GetBytes($"SEND_ONCE {_lircRemote} {cmd}\n");
                    SendToLIRC(cmdBytes);
                }
            }

            Thread.Sleep(250);
        }

        public void SendToLIRC(byte[] cmd)
        {
            bool tryAgain = false;
            try
            {
                _lircSocket.Send(cmd);

                if ((_lircSocket.Poll(1000, SelectMode.SelectRead) && (_lircSocket.Available == 0)) || !_lircSocket.Connected)
                {
                    throw new Exception();
                }
            }
            catch
            {
                tryAgain = true;
            }

            if(tryAgain)
            {
                try
                {
                    Thread.Sleep(75);
                    ConnectToLIRC();

                    _lircSocket.Send(cmd);

                    if ((_lircSocket.Poll(1000, SelectMode.SelectRead) && (_lircSocket.Available == 0)) || !_lircSocket.Connected)
                    {
                        throw new Exception();
                    }
                }
                catch
                {
                    _errorStatus = $"({DateTime.Now:h:mm:ss tt}) Failed sending command to LIRC at: {_lircIP}:{_lircPort}";
                }
            }
        }

        private void ConnectToLIRC()
        {
            if (_lircSocket != null)
            {
                try
                {
                    _lircSocket.Shutdown(SocketShutdown.Both);
                    _lircSocket.Close();
                }
                catch { }
            }

            try
            {
                IPAddress ipAddress = IPAddress.Parse(_lircIP);
                IPEndPoint remoteEP = new IPEndPoint(ipAddress, Convert.ToInt32(_lircPort));
                _lircSocket = new Socket(ipAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

                IAsyncResult result = _lircSocket.BeginConnect(remoteEP, null, null);
                result.AsyncWaitHandle.WaitOne(1000);

                if (!_lircSocket.Connected)
                {

                    throw new Exception();
                }

                _lircSocket.EndConnect(result);
            }
            catch
            {
                _errorStatus = $"({DateTime.Now:h:mm:ss tt}) Cannot connect to LIRC at: {_lircIP}:{_lircPort}";
            }

            Thread.Sleep(25);
        }

        private void LoadVideoTimecodes(string fileName, string filePath)
        {
            string validFileName = null;
            _offset = 0;

            //look for wind track in windtrack folder
            try
            {
                if (File.Exists(Path.Combine(_videoTimecodePath, fileName + ".txt")))
                {
                    validFileName = Path.Combine(_videoTimecodePath, fileName + ".txt");
                }
            }
            catch { }

            //if it wasnt in the windtrack folder, check the movie folder
            if (string.IsNullOrEmpty(validFileName))
            {
                try
                {
                    if (File.Exists(Path.Combine(filePath, fileName + ".txt")))
                    {
                        validFileName = Path.Combine(filePath, fileName + ".txt");
                    }
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(validFileName))
            {
                _windtrackError = null;

                SetTransmitters();

                _videoTimeCodes = new List<Tuple<TimeSpan, string>>();
                _windTrackHeader = "";

                string[] lines = File.ReadAllLines(validFileName);
                string lastCmd = "OFF";
                double rawPrevTime = -500;
                double actualPrevTime = -500;

                double globalOffsetMS = Convert.ToDouble(_globalOffsetMS);
                double spinupOffsetMS = Convert.ToDouble(_spinupOffsetMS);
                double spindownOffsetMS = Convert.ToDouble(_spindownOffsetMS);

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];

                    //header lines
                    if (line.StartsWith("#") && _videoTimeCodes.Count == 0)
                    {
                        _windTrackHeader += line.TrimStart(new[] { '#', ' ' }) + "<br \\>";

                        //offset line
                        if (line.Contains("Offset:"))
                        {
                            try
                            {
                                TimeSpan ts = TimeSpan.Parse(line.Substring(line.IndexOf('(') + 1, line.LastIndexOf(')') - line.IndexOf('(') - 1));
                                _offset = ts.TotalMilliseconds;
                            }
                            catch { }
                        }
                    }
                    //non-comment or blank lines
                    else if (!line.StartsWith(@"\\") && !line.StartsWith(@"//") && line.Length > 0)
                    {
                        string[] lineData = line.Split(',');

                        double? timeCode = null;
                        try
                        {
                            timeCode = TimeSpan.Parse(lineData[0]).TotalMilliseconds - globalOffsetMS;
                        }
                        catch
                        {
                            if (_windtrackError is null)
                            {
                                _windtrackError = $"Bad timecode on line {i+1}: {line}";
                            }
                        }

                        if(timeCode != null)
                        {
                            //apply special offset if enabled
                            if (_offsetEnabled)
                            {
                                timeCode += _offset;
                                rawPrevTime += _offset;
                            }
                            //skip out of order line and error
                            if (timeCode < rawPrevTime)
                            {
                                if (_windtrackError is null)
                                {
                                    _windtrackError = $"Timecode on line {i+1} is out of order";
                                }
                                break;
                            }

                            rawPrevTime = (double)timeCode;

                            //if command comes after OFF, add spinup offset
                            if (lastCmd.Contains("OFF"))
                            {
                                timeCode -= spinupOffsetMS;
                            }
                            //if command is OFF, add spindown offset
                            else if (lineData[1].Contains("OFF"))
                            {
                                timeCode -= spindownOffsetMS;
                            }
                            //if offset makes timecode invalid, fix it
                            if (timeCode < actualPrevTime+500)
                            {
                                timeCode = actualPrevTime + 500;
                            }
                            //keep clearing the list if the timecode is less than or equal to 0 so that we only end up with 1 timecode at 0 at the start
                            if(timeCode <= 0)
                            {
                                _videoTimeCodes.Clear();
                                timeCode = 0;
                            }

                            string cmds = "";
                            for (int j = 1; j < lineData.Length; j++)
                            {
                                cmds += lineData[j] + ",";
                            }

                            _videoTimeCodes.Add(new Tuple<TimeSpan, string>(TimeSpan.FromMilliseconds((double)timeCode), cmds.Trim(',')));

                            lastCmd = lineData[1];
                            actualPrevTime = (double)timeCode;
                        }
                    }
                }
            }
            else
            {
                _videoTimeCodes = null;
            }
        }

        private void SetTransmitters()
        {
            if (_OS != "win")
            {
                string irChannels = "";
                if (_irChan1 == "true")
                {
                    irChannels += "1 ";
                }
                if (_irChan2 == "true")
                {
                    irChannels += "2 ";
                }
                if (_irChan3 == "true")
                {
                    irChannels += "3 ";
                }
                if (_irChan4 == "true")
                {
                    irChannels += "4 ";
                }

                byte[] data = Encoding.ASCII.GetBytes($"SET_TRANSMITTERS {irChannels}\n");
                Console.WriteLine($"SET_TRANSMITTERS {irChannels}\n");

                SendToLIRC(data);
            }
        }
    }
}