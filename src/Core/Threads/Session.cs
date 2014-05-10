// <airvpn_source_header>
// This file is part of AirVPN Client software.
// Copyright (C)2014-2014 AirVPN (support@airvpn.org) / https://airvpn.org )
//
// AirVPN Client is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// AirVPN Client is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with AirVPN Client. If not, see <http://www.gnu.org/licenses/>.
// </airvpn_source_header>

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web;
using System.Xml;
using AirVPN.Core;


namespace AirVPN.Core.Threads
{
	public class Session : AirVPN.Core.Thread
	{
		private Process m_processOpenVpn;
		private Process m_processProxy;

		private Socket m_openVpnManagementSocket;
		private List<string> m_openVpnManagementCommands = new List<string>();
		private List<string> m_openVpnManagementStatisticsLines = new List<string>();
		
		private string m_reset = "";
		private int m_proxyPort = 0;
		private NetworkInterface m_interfaceTun;
		private int m_timeLastStatus = 0;
		private TemporaryFile m_fileSshKey;
		private TemporaryFile m_fileSslConfig;
		private TemporaryFile m_fileOvpn;

		public override void OnRun()
		{
			CancelRequested = false;

			string sessionLastServer = "";

			bool oneConnectionReached = false;

			for (; CancelRequested == false; )
			{
				RouteScope routeScope = null;

				bool allowed = true;
				string waitingMessage = "";
				int waitingSecs = 0;

				try
				{
					// -----------------------------------
					// Phase 1: Initialization and start
					// -----------------------------------

					
					if ((Engine.NextServer == null) && (Pinger.Instance.GetEnabled()) && (Engine.PingerValid() == false))
					{
						Engine.WaitMessageSet(Messages.WaitingLatencyTests);
						for (; ; )
						{
							if (Engine.PingerValid())
								break;
							if (CancelRequested)
								break;
							Sleep(100);
						}
					}

					if (CancelRequested)
						continue;

					m_openVpnManagementCommands.Clear();

					string protocol = Engine.Storage.Get("mode.protocol").ToUpperInvariant();
					int port = Engine.Storage.GetInt("mode.port");
					int alt = Engine.Storage.GetInt("mode.alt");

					if (protocol == "SSH")
					{
						m_proxyPort = Engine.Storage.GetInt("ssh.port");
						if (m_proxyPort == 0)
							m_proxyPort = RandomGenerator.GetInt(1024, 64 * 1024);
					}
					else if (protocol == "SSL")
					{
						m_proxyPort = Engine.Storage.GetInt("ssl.port");
						if (m_proxyPort == 0)
							m_proxyPort = RandomGenerator.GetInt(1024, 64 * 1024);
					}
					else
					{
						m_proxyPort = 0;
					}

					Engine.CurrentServer = Engine.NextServer;
					Engine.NextServer = null;
					if (Engine.CurrentServer == null)
					{
						if (Engine.Storage.GetBool("servers.locklast"))
							Engine.CurrentServer = Engine.PickServer(sessionLastServer);
						else
							Engine.CurrentServer = Engine.PickServer(null);
					}


					if (Engine.CurrentServer == null)
					{
						allowed = false;
						Engine.Log(Core.Engine.LogType.Fatal, "No server available.");
						RequestStop();						
					}

					// Checking auth user status.
					// Only to avoid a generic AUTH_FAILED. For that we don't report here for ex. the sshtunnel keys.
					if (allowed)
					{
						Engine.WaitMessageSet(Messages.AuthorizeConnect);

						Dictionary<string, string> parameters = new Dictionary<string, string>();
						parameters["act"] = "connect";
						parameters["server"] = Engine.CurrentServer.Name;
						parameters["protocol"] = protocol;
						parameters["port"] = port.ToString();
						parameters["alt"] = alt.ToString();

						XmlDocument xmlDoc = AirExchange.Fetch(parameters);
						if (xmlDoc != null) // Note: If failed, continue.
						{
							string userMessage = Utils.XmlGetAttributeString(xmlDoc.DocumentElement, "message", "");
							if (userMessage != "")
							{
								allowed = false;
								string userMessageAction = Utils.XmlGetAttributeString(xmlDoc.DocumentElement, "message_action", "");
								if (userMessageAction == "stop")
								{
									Engine.Log(Core.Engine.LogType.Fatal, userMessage);
									RequestStop();
								}
								else if (userMessageAction == "next")
								{
									Engine.CurrentServer.Penality += Engine.Storage.GetInt("advanced.penality_on_error");
									waitingMessage = userMessage + ", next in {1} sec.";
									waitingSecs = 5;
								}
								else 
								{
									waitingMessage = userMessage + ", retry in {1} sec.";
									waitingSecs = 10;
								}
							}
						}
					}

					if (allowed)
					{
						sessionLastServer = Engine.CurrentServer.Name;
						Engine.Storage.Set("servers.last", Engine.CurrentServer.Name);


						if (Engine.CurrentServer.ServerType == 2)
						{
							// Routing servers have only 443-UDP-1ip
							protocol = "UDP";
							port = 443;
							alt = 0;
						}

						Engine.BuildOVPN(protocol, port, alt, m_proxyPort);

						routeScope = new RouteScope(Engine.ConnectedEntryIP);

						Engine.RunEventCommand("vpn.pre");

						Engine.WaitMessageSet("Connecting to " + Engine.CurrentServer.PublicName);

						if (protocol == "SSH")
						{
							StartSshProcess();
						}
						else if (protocol == "SSL")
						{
							StartSslProcess();
						}
						else if ((protocol == "TCP") || (protocol == "UDP"))
						{
							StartOpenVpnProcess();
						}

						int waitingSleep = 100; // To avoid CPU stress

						m_reset = "";

						// -----------------------------------
						// Phase 2: Waiting connection
						// -----------------------------------

						for (; ; )
						{
							if (m_reset != "")
								break;

							if (Engine.IsConnected())
								break;

							Sleep(waitingSleep);
						}

						// -----------------------------------
						// Phase 3 - Running
						// -----------------------------------

						if (m_reset == "")
						{
							oneConnectionReached = true;

							Engine.RunEventCommand("vpn.up");

							for (; ; )
							{
								int timeNow = Utils.UnixTimeStamp();

								if (Engine.IsConnected() == false)
									throw new Exception("Unexpected.");

								ProcessOpenVpnManagement();

								if (timeNow - m_timeLastStatus >= 1)
								{
									m_timeLastStatus = timeNow;
																		
									// Update traffic stats
									if (m_interfaceTun != null)
									{
										Int64 read = m_interfaceTun.GetIPv4Statistics().BytesReceived;
										Int64 write = m_interfaceTun.GetIPv4Statistics().BytesSent;

										if (Engine.ConnectedLastRead != -1)
										{
											int delta = Engine.ConnectedLastStatsTick.Reset();
											if (delta > 0)
											{
												Engine.ConnectedLastDownloadStep = (1000 * (read - Engine.ConnectedLastRead)) / delta;
												Engine.ConnectedLastUploadStep = (1000 * (write - Engine.ConnectedLastWrite)) / delta;
											}
										}

										Engine.ConnectedLastRead = read;
										Engine.ConnectedLastWrite = write;

										Engine.Instance.Stats.Charts.Hit(Engine.ConnectedLastDownloadStep, Engine.ConnectedLastUploadStep);

										Engine.OnRefreshUi(Core.Engine.RefreshUiMode.Stats);
									}
									else if (Storage.Simulate)
									{
										Engine.Instance.Stats.Charts.Hit(15354, 2525);

										Engine.OnRefreshUi(Core.Engine.RefreshUiMode.Stats);
									}

								}

								
								// Need stop?
								bool StopRequest = false;

								if (m_reset == "RETRY")
								{
									StopRequest = true;
								}

								if (m_reset == "ERROR")
								{
									Engine.CurrentServer.Penality += Engine.Storage.GetInt("advanced.penality_on_error");
									StopRequest = true;
								}

								if (Engine.NextServer != null)
								{
									StopRequest = true;
								}

								if (Engine.SwitchServer != false)
								{
									Engine.SwitchServer = false;
									StopRequest = true;
								}

								if (CancelRequested)
									StopRequest = true;

								if (StopRequest)
									break;

								Sleep(waitingSleep);
							}
						}

						// -----------------------------------
						// Phase 4 - Start disconnection
						// -----------------------------------

						Engine.SetConnected(false);

						Engine.WaitMessageSet("Disconnecting");

						if (Storage.Simulate)
						{
							if (m_processOpenVpn.HasExited == false)
								m_processOpenVpn.Kill();
						}

						// -----------------------------------
						// Phase 5 - Waiting disconnection
						// -----------------------------------

						TimeDelta DeltaSigTerm = new TimeDelta();

						for (; ; )
						{
							try
							{
								// As explained here: http://stanislavs.org/stopping-command-line-applications-programatically-with-ctrl-c-events-from-net/
								// there isn't any .Net/Mono clean method to send a signal term to a Windows console-only application. So a brutal Kill is performed when there isn't any alternative.
								// TODO: Maybe optimized under Linux.

								// Simulation process
								if ((Storage.Simulate) && (m_processOpenVpn != null) && (m_processOpenVpn.HasExited == false))
									m_processOpenVpn.Kill();

								// OpenVPN process completed, but management socket still opened. Strange, but happen. Closing socket.
								if ((m_processOpenVpn != null) && (m_openVpnManagementSocket != null) && (m_processOpenVpn.HasExited == true) && (m_openVpnManagementSocket.Connected))
									m_openVpnManagementSocket.Close();

								// OpenVPN process still exists, but management socket is not connected. We can't tell to OpenVPN to do a plain disconnection, force killing.
								if ((m_processOpenVpn != null) && (m_processOpenVpn.HasExited == false))
								{
									if ((m_openVpnManagementSocket == null) || (m_openVpnManagementSocket.Connected == false))
										m_processOpenVpn.Kill();
								}

								// Proxy (SSH/SSL) process								
								if ((m_processProxy != null) && (m_processOpenVpn != null) && (m_processProxy.HasExited == false) && (m_processOpenVpn.HasExited == true))
									m_processProxy.Kill();

								// Start a clean disconnection
								if ((m_processOpenVpn != null) && (m_openVpnManagementSocket != null) && (m_processOpenVpn.HasExited == false) && (m_openVpnManagementSocket.Connected))
								{
									if (DeltaSigTerm.Elapsed(10000)) // Try a SIGTERM every 10 seconds // TOTEST
									{
										SendManagementCommand("signal SIGTERM");
										ProcessOpenVpnManagement();
									}
								}
							}
							catch (Exception e)
							{
								Engine.Log(Core.Engine.LogType.Warning, e);
							}

							bool exit = true;

							if ((m_openVpnManagementSocket != null) && (m_openVpnManagementSocket.Connected))
								exit = false;

							if ((m_processProxy != null) && (m_processProxy.HasExited == false))
								exit = false;

							if ((m_processOpenVpn != null) && (m_processOpenVpn.HasExited == false))
								exit = false;

							if (exit)
								break;

							Sleep(waitingSleep);
						}

						// -----------------------------------
						// Phase 6: Cleaning, waiting before retry.
						// -----------------------------------

						Engine.Log(Engine.LogType.Verbose, Messages.ConnectionStop);

						Engine.RunEventCommand("vpn.down");

						// Closing temporary files
						if (m_fileSshKey != null)
							m_fileSshKey.Close();
						if (m_fileSslConfig != null)
							m_fileSslConfig.Close();
						if (m_fileOvpn != null)
							m_fileOvpn.Close();
					}

					

				}
				catch (Exception e)
				{
					// Warning: Avoid to reach this catch: unpredicable status of running processes.
					Engine.SetConnected(false);

					Engine.Log(Core.Engine.LogType.Warning, e);					
				}

				if (routeScope != null)
					routeScope.End();


				if (m_reset == "AUTH_FAILED")
				{
					waitingMessage = "Auth failed, retry in {1} sec.";
					waitingSecs = 10;
				}
				else if (m_reset == "ERROR")
				{
					waitingMessage = "Restart in {1} sec.";
					waitingSecs = 3;
				}

				if (waitingSecs > 0)
				{
					for (int i = 0; i < waitingSecs; i++)
					{
						Engine.WaitMessageSet(Messages.Format(waitingMessage, (waitingSecs - i).ToString()), false);
						if (CancelRequested)
							break;

						Sleep(1000);
					}
				}
			}

			if (oneConnectionReached == false)
			{
				if (CancelRequested)
				{
					Engine.Log(Engine.LogType.Info, Messages.SessionCancel);
				}
				else
				{
					Engine.Log(Engine.LogType.Error, Messages.SessionFailed);
				}
			}
			
			Engine.Instance.WaitMessageClear();
			
			Engine.CurrentServer = null; 
		}

		private void StartSshProcess()
		{
			string fileKeyExtension = "";
			if (Platform.Instance.IsUnixSystem())
				fileKeyExtension = "key";
			else
				fileKeyExtension = "ppk";
			

			m_fileSshKey = new TemporaryFile(fileKeyExtension);
			File.WriteAllText(m_fileSshKey.Path, Utils.XmlGetAttributeString(Engine.Storage.User, "ssh_" + fileKeyExtension, ""));
			
			if (Platform.Instance.IsUnixSystem())
			{
				Platform.Instance.ShellCmd("chmod 700 \"" + m_fileSshKey.Path + "\"");
			}
			
			string arguments = "";

			arguments += " -i \"" + m_fileSshKey.Path + "\" -L " + Conversions.ToString(m_proxyPort) + ":127.0.0.1:2018 sshtunnel@" + Engine.ConnectedEntryIP;
			if (Platform.Instance.IsUnixSystem())
				arguments += " -p " + Engine.ConnectedPort; // ssh use -p
			else
				arguments += " -P " + Engine.ConnectedPort; // plink use -P			
				
			if (Platform.Instance.IsUnixSystem())
				arguments += " -o UserKnownHostsFile=/dev/null -o StrictHostKeyChecking=no"; // TOOPTIMIZE: To bypass key confirmation. Not the best approach.
			arguments += " -N -T -v";

			Engine.Log(Engine.LogType.Warning, arguments);
			
			m_processProxy = new Process();
			m_processProxy.StartInfo.FileName = Software.SshPath;
			m_processProxy.StartInfo.Arguments = arguments;
			m_processProxy.StartInfo.WorkingDirectory = Utils.GetTempPath();

			m_processProxy.StartInfo.Verb = "run";
			m_processProxy.StartInfo.CreateNoWindow = true;
			m_processProxy.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
			m_processProxy.StartInfo.UseShellExecute = false;
			m_processProxy.StartInfo.RedirectStandardInput = true;
			m_processProxy.StartInfo.RedirectStandardError = true;
			m_processProxy.StartInfo.RedirectStandardOutput = true;

			m_processProxy.ErrorDataReceived += new DataReceivedEventHandler(ProcessSshOutputDataReceived);
			m_processProxy.OutputDataReceived += new DataReceivedEventHandler(ProcessSshOutputDataReceived);

			m_processProxy.Start();
			
			m_processProxy.BeginOutputReadLine();
			m_processProxy.BeginErrorReadLine();
		}

		private void StartSslProcess()
		{
			string sslConfig = "";

			if (Platform.Instance.IsUnixSystem())
			{
				sslConfig += "output = /dev/stdout\n";
				sslConfig += "pid = /tmp/stunnel4.pid\n";
			}
			sslConfig += "options = NO_SSLv2\n";
			sslConfig += "client = yes\n";
			sslConfig += "debug = 6\n";
			sslConfig += "\n";
			sslConfig += "[openvpn]\n";
			sslConfig += "accept = 127.0.0.1:" + Conversions.ToString(m_proxyPort) + "\n";
			sslConfig += "connect = " + Engine.ConnectedEntryIP + ":" + Engine.ConnectedPort + "\n";
			sslConfig += "TIMEOUTclose = 0\n";			
			sslConfig += "\n";
			
			m_fileSslConfig = new TemporaryFile("ssl");
			string sslConfigPath = m_fileSslConfig.Path;
			Utils.SaveFile(sslConfigPath, sslConfig);

			m_processProxy = new Process();
			m_processProxy.StartInfo.FileName = Software.SslPath;
			m_processProxy.StartInfo.Arguments = sslConfigPath;
			m_processProxy.StartInfo.WorkingDirectory = Utils.GetTempPath();

			m_processProxy.StartInfo.Verb = "run";
			m_processProxy.StartInfo.CreateNoWindow = true;
			m_processProxy.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
			m_processProxy.StartInfo.UseShellExecute = false;
			m_processProxy.StartInfo.RedirectStandardInput = true;
			m_processProxy.StartInfo.RedirectStandardError = true;
			m_processProxy.StartInfo.RedirectStandardOutput = true;
						
			m_processProxy.ErrorDataReceived += new DataReceivedEventHandler(ProcessSslOutputDataReceived);
			m_processProxy.OutputDataReceived += new DataReceivedEventHandler(ProcessSslOutputDataReceived);

			m_processProxy.Start();
			
			m_processProxy.BeginOutputReadLine();
			m_processProxy.BeginErrorReadLine();			

		}

		private void StartOpenVpnProcess()
		{
			m_fileOvpn = new TemporaryFile("ovpn");
			string ovpnPath = m_fileOvpn.Path;
			Utils.SaveFile(ovpnPath, Engine.ConnectedOVPN);

			m_processOpenVpn = new Process();
			m_processOpenVpn.StartInfo.FileName = Software.OpenVpnPath;
			m_processOpenVpn.StartInfo.Arguments = "";
			m_processOpenVpn.StartInfo.WorkingDirectory = Utils.GetTempPath();

			if (Storage.Simulate)
			{
				m_processOpenVpn.StartInfo.FileName = "Simulate.exe";
				Sleep(1000);
				Engine.SetConnected(true);
			}

			m_processOpenVpn.StartInfo.Arguments = "--config \"" + ovpnPath + "\" ";

			m_processOpenVpn.StartInfo.Verb = "run";
			m_processOpenVpn.StartInfo.CreateNoWindow = true;
			m_processOpenVpn.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
			m_processOpenVpn.StartInfo.UseShellExecute = false;
			m_processOpenVpn.StartInfo.RedirectStandardInput = true;
			m_processOpenVpn.StartInfo.RedirectStandardError = true;
			m_processOpenVpn.StartInfo.RedirectStandardOutput = true;

			m_processOpenVpn.OutputDataReceived += new DataReceivedEventHandler(ProcessOpenVpnOutputDataReceived);
			m_processOpenVpn.ErrorDataReceived += new DataReceivedEventHandler(ProcessOpenVpnOutputDataReceived);

			m_processOpenVpn.Start();

			m_processOpenVpn.BeginOutputReadLine();
			m_processOpenVpn.BeginErrorReadLine();
		}

		public void SendManagementCommand(string Cmd)
		{
			if (Cmd == "k1")
			{
				m_openVpnManagementSocket.Close();
			}
			else if (Cmd == "k2")
			{
				m_processOpenVpn.Kill();
			}
			else if (Cmd == "k3")
			{
				m_processProxy.Kill();
			}

			if (m_openVpnManagementSocket == null)
				return;

			if (m_openVpnManagementSocket.Connected == false)
				return;

			lock (this)
			{
				m_openVpnManagementCommands.Add(Cmd);
			}
		}

		void ProcessSshOutputDataReceived(object sender, DataReceivedEventArgs e)
		{
			// TOCHECK: Must wait until a \n ?
			if (e.Data != null)
			{
				string message = e.Data.ToString();

				ProcessOutput("SSH", message);
			}
		}

		void ProcessSslOutputDataReceived(object sender, DataReceivedEventArgs e)
		{
			// TOCHECK: Must wait until a \n ?
			if (e.Data != null)
			{
				string message = e.Data.ToString();

				// Remove STunnel timestamp
				message = System.Text.RegularExpressions.Regex.Replace(message, "^\\d{4}\\.\\d{2}\\.\\d{2}\\s\\d{2}:\\d{2}:\\d{2}\\sLOG\\d{1}\\[\\d{0,6}:\\d{0,60}\\]:\\s", "");

				ProcessOutput("SSL", message);
			}
		}

		void ProcessOpenVpnOutputDataReceived(object sender, DataReceivedEventArgs e)
		{
			// TOCHECK: Must wait until a \n ?
			if (e.Data != null)
			{
				string message = e.Data.ToString();

				// Remove OpenVPN timestamp
				message = System.Text.RegularExpressions.Regex.Replace(message, "^\\w{3}\\s\\w{3}\\s\\d{1,2}\\s\\d{1,2}:\\d{1,2}:\\d{1,2}\\s\\d{2,4}\\s", "");
				
				ProcessOutput("OpenVPN", message);
			}
		}

		void ProcessOpenVpnManagement()
		{
			try
			{
				// Fetch OpenVPN Management
				if (m_openVpnManagementSocket != null)
				{
					if (m_openVpnManagementSocket.Connected == false)
						throw new Exception("OpenVPN Management disconnected.");

					lock (this)
					{
						foreach (string command in m_openVpnManagementCommands)
						{
							if (command != "status")
								Engine.Log(Engine.LogType.Verbose, "Management - Send '" + command + "'");

							string MyCmd = command + "\n";
							Byte[] bufS = new byte[1024 * 16];
							int lenS = Encoding.ASCII.GetBytes(MyCmd, 0, MyCmd.Length, bufS, 0);
							
							m_openVpnManagementSocket.Send(bufS, lenS, SocketFlags.None);							
						}
						m_openVpnManagementCommands.Clear();
					}

					// Fetch OpenVPN Management
					if (m_openVpnManagementSocket.Available != 0)
					{
						Byte[] buf = new byte[1024 * 16];
						int bytes = m_openVpnManagementSocket.Receive(buf, buf.Length, 0);

						string data = Encoding.ASCII.GetString(buf, 0, bytes);

						ProcessOutput("Management", data);
					}
				}
			}
			catch (Exception ex)
			{
				Engine.Log(Engine.LogType.Warning, ex);

				m_reset = "ERROR";
			}
		}

		void ProcessOutput(string source, string message)
		{
			try
			{
				Platform.Instance.OnDaemonOutput(source, message);

				if (source == "OpenVPN")
				{
					bool log = true;
					if (message.IndexOf("MANAGEMENT: CMD 'status'") != -1)
						log = false;

					if (message.IndexOf("Connection reset, restarting") != -1)
					{
						m_reset = "ERROR";
					}

					if (message.IndexOf("MANAGEMENT: Socket bind failed on local address") != -1)
					{
						Engine.Log(Engine.LogType.Verbose, Messages.AutoPortSwitch);

						Engine.Storage.SetInt("openvpn.management_port", Engine.Storage.GetInt("openvpn.management_port") + 1);

						m_reset = "RETRY";
					}

					if (message.IndexOf("AUTH_FAILED") != -1)
					{
						Engine.Log(Engine.LogType.Warning, Messages.AuthFailed);

						m_reset = "AUTH_FAILED";
					}

					if (message.IndexOf("MANAGEMENT: TCP Socket listening on") != -1)
					{
					}

					if (message.IndexOf("TLS: tls_process: killed expiring key") != -1)
					{
						Engine.Log(Engine.LogType.Info, Messages.RenewingTls);
					}

					if (message.IndexOf("Initialization Sequence Completed With Errors") != -1)
					{
						m_reset = "ERROR";
					}

					if (message.IndexOf("Initialization Sequence Completed") != -1)
					{
						Engine.Log(Core.Engine.LogType.Verbose, Messages.ConnectionStartManagement);

						m_openVpnManagementSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
						m_openVpnManagementSocket.Connect("127.0.0.1", Engine.Storage.GetInt("openvpn.management_port"));
						m_openVpnManagementSocket.SendTimeout = 5000;
						m_openVpnManagementSocket.ReceiveTimeout = 5000;
					}

					if (message.IndexOf("Client connected from [AF_INET]127.0.0.1") != -1)
					{
						Engine.WaitMessageSet(Messages.ConnectionFlushDNS);

						Platform.Instance.FlushDNS();

						Engine.WaitMessageSet("Checking");

						if (Engine.Storage.GetBool("advanced.check.route"))
						{
							Engine.Log(Core.Engine.LogType.Verbose, Messages.ConnectionCheckingRoute);

							if (Engine.CurrentServer.IpEntry == Engine.CurrentServer.IpExit)
							{
								Engine.Log(Core.Engine.LogType.Warning, Messages.ConnectionCheckingRouteNotAvailable); 
							}
							else
							{
								string destIp = Engine.CurrentServer.IpExit;
								RouteScope routeScope = new RouteScope(destIp);
								XmlDocument xmlDoc = Engine.XmlFromUrl("https://" + destIp + ":88/check.php");
								routeScope.End();
								string VpnIp = xmlDoc.DocumentElement.Attributes["ip"].Value;
								Engine.ConnectedServerTime = Conversions.ToInt64(xmlDoc.DocumentElement.Attributes["time"].Value);
								Engine.ConnectedClientTime = Utils.UnixTimeStamp();

								if (VpnIp != Engine.ConnectedVpnIp)
								{
									Engine.Log(Engine.LogType.Error, Messages.ConnectionCheckingRouteFailed);
									m_reset = "ERROR";
								}
							}

							if (m_reset == "")
							{
								string destIp = Engine.ConnectedEntryIP;
								XmlDocument xmlDoc = Engine.XmlFromUrl("https://" + destIp + ":88/check.php");
								Engine.ConnectedRealIp = xmlDoc.DocumentElement.Attributes["ip"].Value;
								Engine.ConnectedServerTime = Conversions.ToInt64(xmlDoc.DocumentElement.Attributes["time"].Value);
								Engine.ConnectedClientTime = Utils.UnixTimeStamp();
							}
						}
						else
						{
							Engine.ConnectedRealIp = "";
							Engine.ConnectedServerTime = 0;
						}

						// DNS test
						if ((m_reset == "") && (Engine.Storage.GetBool("advanced.check.dns")))
						{
							Engine.Log(Core.Engine.LogType.Verbose, Messages.ConnectionCheckingDNS);

							bool failed = true;
							IPHostEntry entry = Dns.GetHostEntry(Engine.Storage.GetManifestKeyValue("dnscheck_host", ""));

							if (entry != null)
							{
								if (entry.AddressList.Length == 1)
								{
									string Ip1 = entry.AddressList[0].ToString();
									string Ip2 = Engine.Storage.GetManifestKeyValue("dnscheck_res2", "");
									if (Ip1 == Ip2)
										failed = false;
								}
							}

							if (failed)
							{
								Engine.Log(Engine.LogType.Error, Messages.ConnectionCheckingDNSFailed);
								m_reset = "ERROR";
							}
						}

						if (m_reset == "")
						{
							Engine.Log(Engine.LogType.InfoImportant, Messages.ConnectionConnected);
							Engine.SetConnected(true);
						}
					}

					// Windows
					if(Platform.Instance.IsUnixSystem() == false)
					{
						Match match = Regex.Match(message, "TAP-.*? device \\[(.*?)\\] opened: \\\\\\\\\\.\\\\Global\\\\(.*?).tap");
						if (match.Success)
						{
							Engine.ConnectedVpnInterfaceName = match.Groups[1].Value;
							Engine.ConnectedVpnInterfaceId = match.Groups[2].Value;

							NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
							foreach (NetworkInterface adapter in interfaces)
							{
								if (adapter.Id == Engine.ConnectedVpnInterfaceId)
									m_interfaceTun = adapter;
							}
						}
					}

					// Unix
					if (Platform.Instance.IsUnixSystem())
					{
						Match match = Regex.Match(message, "TUN/TAP device (.*?) opened");
						if (match.Success)
						{
							Engine.ConnectedVpnInterfaceName = match.Groups[1].Value;
							Engine.ConnectedVpnInterfaceId = match.Groups[1].Value;

							NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
							foreach (NetworkInterface adapter in interfaces)
							{
								if (adapter.Id == Engine.ConnectedVpnInterfaceId)
									m_interfaceTun = adapter;
							}
						}
					}

					{
						Match match = Regex.Match(message, "dhcp-option DNS ([0-9\\.]*?),");
						if (match.Success)
						{
							Engine.ConnectedVpnDns = match.Groups[1].Value;
						}
					}

					{
						Match match = Regex.Match(message, "ifconfig ([0-9\\.]+) ([0-9\\.]+)");
						if (match.Success)
						{
							Engine.ConnectedVpnIp = match.Groups[1].Value;
							Engine.ConnectedVpnGateway = match.Groups[2].Value;
						}
					}

					if (log)
						Engine.Log(Engine.LogType.Verbose, source + " > " + message);
				}
				else if (source == "SSH")
				{
					bool log = true;

					// Windows PuTTY
					if (message.IndexOf("enter \"y\" to update PuTTY's cache and continue connecting") != -1)
						m_processProxy.StandardInput.WriteLine("y");

					if (message.IndexOf("If you trust this host, enter \"y\" to add the key to") != -1)
						m_processProxy.StandardInput.WriteLine("y");
					
					if (message == "Access granted") // PLink Windows
					{
						StartOpenVpnProcess();
					}

					if (message.StartsWith("Authenticated to")) // SSH Linux
					{
						StartOpenVpnProcess();
					}

					if (log)
						Engine.Log(Engine.LogType.Verbose, source + " > " + message);
				}
				else if (source == "SSL")
				{
					bool log = true;

					if (message.IndexOf("Configuration successful") != -1)
					{
						StartOpenVpnProcess();
					}

					if (log)
						Engine.Log(Engine.LogType.Verbose, source + " > " + message);
				}
				else if (source == "Management")
				{
					ProcessOutputManagement(source, message);
				}
			}
			catch (Exception ex)
			{
				Engine.Log(Engine.LogType.Warning, ex);

				m_reset = "ERROR";
			}
		}

		public void ProcessOutputManagement(string source, string message)
		{
			string[] lines = message.Split('\n');
			for (int i = 0; i < lines.Length; i++)
			{
				lines[i] = lines[i].Trim();

				string line = lines[i];

				if (line == "")
				{
				}
				else if (line == "OpenVPN STATISTICS")
				{
					m_openVpnManagementStatisticsLines.Add(line);
				}
				else if (line == "END")
				{
					if (m_openVpnManagementStatisticsLines.Count != 0) // If 0, 'END' refer to another command.
					{
						// Process statistics
						Int64 read = 0;
						Int64 write = 0;
						String[] readArray = m_openVpnManagementStatisticsLines[4].Split(',');
						String[] writeArray = m_openVpnManagementStatisticsLines[5].Split(',');
						if (readArray.Length == 2)
							read = Conversions.ToInt64(readArray[1]);
						if (writeArray.Length == 2)
							write = Conversions.ToInt64(writeArray[1]);
												
						lock (Engine)
						{
							if (Engine.ConnectedLastRead != -1)
							{
								int delta = Engine.ConnectedLastStatsTick.Reset();
								if (delta > 0)
								{
									Engine.ConnectedLastDownloadStep = (1000 * (read - Engine.ConnectedLastRead)) / delta;
									Engine.ConnectedLastUploadStep = (1000 * (write - Engine.ConnectedLastWrite)) / delta;
								}
							}

							Engine.ConnectedLastRead = read;
							Engine.ConnectedLastWrite = write;
						}

						{
							string countryName = Engine.CurrentServer.CountryName;
							string tooltipText = Constants.Name + " - D: " + Core.Utils.FormatBytes(Engine.ConnectedLastDownloadStep, true, false) + ", U: " + Core.Utils.FormatBytes(Engine.ConnectedLastUploadStep, true, false) + " - " + countryName;

							Engine.Log(Engine.LogType.Realtime, tooltipText);
						}

						Engine.OnRefreshUi(Core.Engine.RefreshUiMode.Stats);

						m_openVpnManagementStatisticsLines.Clear();
					}
				}
				else if (m_openVpnManagementStatisticsLines.Count != 0)
				{
					m_openVpnManagementStatisticsLines.Add(lines[i]);
				}
				else
				{
					Engine.Log(Engine.LogType.Verbose, "OpenVpn Management > " + line);
				}
			}
		}


	}
}