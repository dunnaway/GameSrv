﻿/*
  GameSrv: A BBS Door Game Server
  Copyright (C) 2002-2013  Rick Parrish, R&M Software

  This file is part of GameSrv.

  GameSrv is free software: you can redistribute it and/or modify
  it under the terms of the GNU General Public License as published by
  the Free Software Foundation, either version 3 of the License, or
  any later version.

  GameSrv is distributed in the hope that it will be useful,
  but WITHOUT ANY WARRANTY; without even the implied warranty of
  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
  GNU General Public License for more details.

  You should have received a copy of the GNU General Public License
  along with GameSrv.  If not, see <http://www.gnu.org/licenses/>.
*/
using System;
using System.IO;
using RandM.RMLib;
using System.Globalization;
using System.Threading;
using System.Text.RegularExpressions;
using System.Collections.Generic;

namespace RandM.GameSrv
{
    class ServerThread : RMThread, IDisposable
    {
        private ConnectionType _ConnectionType;
        private bool _Disposed = false;
        private string _LocalAddress;
        private int _LocalPort;

        public event EventHandler BindFailedEvent = null;
        public event EventHandler BoundEvent = null;
        public event EventHandler<ConnectEventArgs> ConnectEvent = null;
        public event EventHandler<StringEventArgs> ErrorMessageEvent = null;
        public event EventHandler<ExceptionEventArgs> ExceptionEvent = null;
        public event EventHandler<StringEventArgs> WarningMessageEvent = null;

        public ServerThread(string ALocalAddress, int ALocalPort, ConnectionType AConnectionType)
        {
            _LocalAddress = ALocalAddress;
            _LocalPort = ALocalPort;
            _ConnectionType = AConnectionType;
            _Paused = false;
        }

        ~ServerThread()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            // This object will be cleaned up by the Dispose method.
            // Therefore, you should call GC.SupressFinalize to
            // take this object off the finalization queue
            // and prevent finalization code for this object
            // from executing a second time.
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            // Check to see if Dispose has already been called.
            if (!_Disposed)
            {
                // If disposing equals true, dispose all managed
                // and unmanaged resources.
                if (disposing)
                {
                    // Dispose managed resources.
                }

                // Call the appropriate methods to clean up
                // unmanaged resources here.
                // If disposing is false,
                // only the following code is executed.

                // Note disposing has been done.
                _Disposed = true;
            }
        }

        private void DisplayAnsi(string fileName, TcpConnection connection, TerminalType terminalType)
        {
            try
            {
                List<string> FileNames = new List<string>();
                if (terminalType == TerminalType.Rip)
                {
                    FileNames.Add(StringUtils.PathCombine(ProcessUtils.StartupPath, "ansi", fileName.ToLower() + ".rip"));
                }
                if ((terminalType == TerminalType.Rip) || (terminalType == TerminalType.Ansi))
                {
                    FileNames.Add(StringUtils.PathCombine(ProcessUtils.StartupPath, "ansi", fileName.ToLower() + ".ans"));
                }
                FileNames.Add(StringUtils.PathCombine(ProcessUtils.StartupPath, "ansi", fileName.ToLower() + ".asc"));

                foreach (string FullFileName in FileNames)
                {
                    if (File.Exists(FullFileName))
                    {
                        connection.Write(FileUtils.FileReadAllText(FullFileName));
                        break;
                    }
                }
            }
            catch (IOException ioex)
            {
                RaiseExceptionEvent("I/O exception displaying " + fileName + ": " + ioex.Message, ioex);
            }
            catch (Exception ex)
            {
                RaiseExceptionEvent("Unknown exception displaying " + fileName + ": " + ex.Message, ex);
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        protected override void Execute()
        {
            using (TcpConnection Connection = new TcpConnection())
            {
                if (Connection.Listen(_LocalAddress, _LocalPort))
                {
                    RaiseBoundEvent();

                    while (!_Stop)
                    {
                        // Accept an incoming connection
                        if (Connection.CanAccept(1000)) // 1 second
                        {
                            try
                            {
                                TcpConnection NewConnection = Connection.AcceptTCP();
                                if (NewConnection != null)
                                {
                                    TerminalType TT = GetTerminalType(NewConnection);
                                    if (IsBannedIP(NewConnection.GetRemoteIP()))
                                    {
                                        DisplayAnsi("IP_BANNED", NewConnection, TT);
                                        RaiseWarningMessageEvent("IP " + NewConnection.GetRemoteIP() + " matches banned IP filter");
                                        NewConnection.Close();
                                    }
                                    else if (_Paused)
                                    {
                                        DisplayAnsi("SERVER_PAUSED", NewConnection, TT);
                                        NewConnection.Close();
                                    }
                                    else
                                    {
                                        ClientThread NewClientThread = new ClientThread();
                                        int NewNode = RaiseConnectEvent(ref NewClientThread);
                                        if (NewNode == 0)
                                        {
                                            NewClientThread.Dispose();
                                            DisplayAnsi("SERVER_BUSY", NewConnection, TT);
                                            NewConnection.Close();
                                        }
                                        else
                                        {
                                            NewClientThread.Start(NewNode, NewConnection, _ConnectionType, TT);
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                RaiseExceptionEvent("Unhandled exception in ServerThread::Execute(): " + ex.Message, ex);
                            }
                        }
                    }
                }
                else
                {
                    RaiseErrorMessageEvent("Server Thread unable to listen on " + _LocalAddress + ":" + _LocalPort);
                    RaiseBindFailedEvent();
                }
            }
        }

        // Logic for this terminal type detection taken from Synchronet's ANSWER.CPP
        private TerminalType GetTerminalType(TcpConnection connection)
        {
            try {
                /* Detect terminal type */
                Thread.Sleep(200);
	            connection.ReadString();		/* flush input buffer */
                connection.Write("\r\n" +		/* locate cursor at column 1 */
			        "\x1b[s" +	                /* save cursor position (necessary for HyperTerm auto-ANSI) */
                    "\x1b[255B" +	            /* locate cursor as far down as possible */
                    "\x1b[255C" +	            /* locate cursor as far right as possible */
                    "\b_" +		                /* need a printable at this location to actually move cursor */
                    "\x1b[6n" +	                /* Get cursor position */
                    "\x1b[u" +	                /* restore cursor position */
                    "\x1b[!_" +	                /* RIP? */
                    "\x1b[0m_" +	            /* "Normal" colors */
                    "\x1b[2J" +	                /* clear screen */
                    "\x1b[H" +	                /* home cursor */
                    "\xC" +		                /* clear screen (in case not ANSI) */
                    "\r"		                /* Move cursor left (in case previous char printed) */
			    );

                char? c = '\0';
                int i = 0;
                string str = "";
                while (i++ < 50)
                { 	/* wait up to 5 seconds for response */
                    c = connection.ReadChar(100);
                    if (connection.ReadTimedOut)
                        continue;
                    if (c == null)
                        continue;
                    c = (char)(c & 0x7f);
                    if (c == 0)
                        continue;
                    i = 0;
                    if (string.IsNullOrEmpty(str) && c != '\x1b')	// response must begin with escape char
                        continue;
                    str += c;
                    if (c == 'R')
                    {   /* break immediately if ANSI response */
                        Thread.Sleep(500);
                        break;
                    }
                }

                while (connection.CanRead(100))
                {
                    str += connection.ReadString();
                }

                if (str.ToUpper().Contains("RIPSCRIP"))
                {
                    return TerminalType.Rip;
                }
                else if (Regex.IsMatch(str, "\\x1b[[]\\d{1,3};\\d{1,3}R"))
                {
                    return TerminalType.Ansi;
                }
            } catch (Exception) {
                // Ignore, we'll just assume ASCII if something bad happens
            }

            return TerminalType.Ascii;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private bool IsBannedIP(string ip)
        {
            try
            {
                string BannedIPsFileName = StringUtils.PathCombine(ProcessUtils.StartupPath, "config", "banned-ips.txt");
                if (File.Exists(BannedIPsFileName))
                {
                    string[] ConnectionOctets = ip.Split('.');
                    if (ConnectionOctets.Length == 4)
                    {
                        string[] BannedIPs = FileUtils.FileReadAllLines(BannedIPsFileName);
                        foreach (string BannedIP in BannedIPs)
                        {
                            if (BannedIP.StartsWith(";")) continue;

                            string[] BannedOctets = BannedIP.Split('.');
                            if (BannedOctets.Length == 4)
                            {
                                bool Match = true;
                                for (int i = 0; i < 4; i++)
                                {
                                    if ((BannedOctets[i] == "*") || (BannedOctets[i] == ConnectionOctets[i]))
                                    {
                                        // We still have a match
                                        continue;
                                    }
                                    else
                                    {
                                        // No longer have a match
                                        Match = false;
                                        break;
                                    }
                                }

                                // If we still have a match after the loop, it's a banned IP
                                if (Match) return true;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                RaiseExceptionEvent("Unable to validate client IP against banned-ips.txt", ex);
            }

            // If we get here, it's an OK IP
            return false;
        }

        private void RaiseBindFailedEvent()
        {
            EventHandler Handler = BindFailedEvent;
            if (Handler != null) Handler(this, EventArgs.Empty);
        }

        private void RaiseBoundEvent()
        {
            EventHandler Handler = BoundEvent;
            if (Handler != null) Handler(this, EventArgs.Empty);
        }

        private int RaiseConnectEvent(ref ClientThread AClientThread)
        {
            EventHandler<ConnectEventArgs> Handler = ConnectEvent;
            if (Handler != null)
            {
                ConnectEventArgs e = new ConnectEventArgs(AClientThread);
                Handler(this, e);
                return e.Node;
            }

            return 0;
        }

        private void RaiseErrorMessageEvent(string AMessage)
        {
            EventHandler<StringEventArgs> Handler = ErrorMessageEvent;
            if (Handler != null) Handler(this, new StringEventArgs(AMessage));
        }

        private void RaiseExceptionEvent(string AMessage, Exception AException)
        {
            EventHandler<ExceptionEventArgs> Handler = ExceptionEvent;
            if (Handler != null) Handler(this, new ExceptionEventArgs(AMessage, AException));
        }

        private void RaiseWarningMessageEvent(string AMessage)
        {
            EventHandler<StringEventArgs> Handler = WarningMessageEvent;
            if (Handler != null) Handler(this, new StringEventArgs(AMessage));
        }
    }
}