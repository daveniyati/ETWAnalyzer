﻿//// SPDX-FileCopyrightText:  © 2023 Siemens Healthcare GmbH
//// SPDX-License-Identifier:   MIT

using ETWAnalyzer.Extract;
using ETWAnalyzer.Extract.Network.Tcp;
using ETWAnalyzer.Extractors.Dns;
using ETWAnalyzer.Infrastructure;
using ETWAnalyzer.TraceProcessorHelpers;
using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Events;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static ETWAnalyzer.Extract.Network.Tcp.TcpStatistics;

namespace ETWAnalyzer.Extractors.TCP
{
    internal class TCPExtractor : ExtractorBase
    {
        /// <summary>
        /// TCP data is stored as generic events which we parse
        /// </summary>
        IPendingResult<IGenericEventDataSource> myGenericEvents;

        /// <summary>
        /// Send events
        /// </summary>
        readonly List<TcpDataSend> mySendEvents = new();


        /// <summary>
        /// Receive events
        /// </summary>
        readonly List<TcpDataTransferReceive> myReceiveEvents = new();

        /// <summary>
        /// Retransmit events
        /// </summary>
        readonly List<TcpRetransmit> myRetransmits = new();

        /// <summary>
        /// Connection requests
        /// </summary>
        readonly List<TcpRequestConnect> myConnections = new();

        /// <summary>
        /// Needed to resolve TCBs for connections which are missing the open event but are not part of TCP rundown data
        /// </summary>
        readonly List<TcpAcceptListenerComplete> myAcceptListenerCompletes = new();


        public TCPExtractor()
        {
        }

        public override void RegisterParsers(ITraceProcessor processor)
        {
            myGenericEvents = processor.UseGenericEvents();
        }

        public override void Extract(ITraceProcessor processor, ETWExtract results)
        {
            using var logger = new PerfLogger("Extract TCPExtractor");
            if (!myGenericEvents.HasResult)
            {
                return;
            }

            foreach (var ev in myGenericEvents.Result.Events.Where(IsValidTcpEvent).OrderBy(x => x.Timestamp).ToArray())
            {
                switch (ev.Id)
                {
                    case TcpETWConstants.TcpDataTransferSendId:
                        OnTpcDataTransferSend(ev);
                        break;
                    case TcpETWConstants.TcpDataTransferReceive:
                        OnTcpDataTransferReceive(ev);
                        break;
                    case TcpETWConstants.TcpDataTransferRetransmitRound:
                        OnRetransmit(ev);
                        break;
                    case TcpETWConstants.TcpRequestConnect:
                        OnConnect(ev, results);
                        break;
                    case TcpETWConstants.TcpTemplateChanged:
                        OnTcpTemplateChanged(ev);
                        break;
                    case TcpETWConstants.TcpTailLossProbe:
                        OnTailLossProbe(ev);
                        break;
                    case TcpETWConstants.TcpConnectionSummary:
                        OnTcpConnectionSummary(ev);
                        break;
                    case TcpETWConstants.TcpConnectionRundown:
                        OnTcpConnectionRundown(ev);
                        break;
                    case TcpETWConstants.TcpAcceptListenerComplete:
                        OnTcpAcceptListenerComplete(ev);
                        break;
                    case TcpETWConstants.TcpCloseTcbRequest:
                        OnClose(ev);
                        break;
                    default:
                        // Do nothing
                        break;
                }
            }


            ILookup<ulong, TcpRequestConnect> connectionsByTcb = myConnections.ToLookup(x => x.Tcb);

            
            foreach(TcpDataSend send in mySendEvents)
            {
               send.Connection = LocateConnection(send.Tcb, send.Timestamp, results, ref connectionsByTcb);
            }

            ILookup<TcpRequestConnect, TcpDataSend> sentByConnection = mySendEvents.ToLookup(x => x.Connection);

            foreach (TcpDataTransferReceive receive in myReceiveEvents)
            {
                receive.Connection = LocateConnection(receive.Tcb, receive.Timestamp, results, ref connectionsByTcb);
            }

            ILookup<TcpRequestConnect, TcpDataTransferReceive> receivedByConnection = myReceiveEvents.ToLookup(x => x.Connection);

            foreach(TcpTemplateChanged templateChange in myTemplateChangedEvents)
            {
                templateChange.Connection = LocateConnection(templateChange.Tcb, templateChange.Timestamp, results, ref connectionsByTcb);
            }

            ILookup<TcpRequestConnect, TcpTemplateChanged> byConnectionTemplateChanges = myTemplateChangedEvents.ToLookup(x => x.Connection);

            Dictionary<TcpRequestConnect, ConnectionIdx> connect2Idx = new();



            // Store connections in ETWExtract
            foreach (TcpRequestConnect tcpconnection in connectionsByTcb.SelectMany(x=>x).OrderBy(x=>x.RemoteIpAndPort.Address) )
            {
                List<string> templates = new();
                foreach(var change in byConnectionTemplateChanges[tcpconnection].OrderBy(x=>x.Timestamp))
                {
                    templates.Add(change.TemplateType.ToString());
                }

                var filterConnection = (IGenericTcpEvent ev) => ev.Connection == tcpconnection;

                ulong bytesReceived = (ulong) receivedByConnection[tcpconnection].Sum(x => (decimal) ((TcpDataTransferReceive) x).NumBytes);


                ulong bytesSent = (ulong)sentByConnection[tcpconnection].Sum(x => (decimal) ((TcpDataSend) x).BytesSent);
                int datagramsReceived = receivedByConnection[tcpconnection].Count();
                int datagramsSent = sentByConnection[tcpconnection].Count();

                TcpConnection connection = new(tcpconnection.Tcb, tcpconnection.LocalIpAndPort, tcpconnection.RemoteIpAndPort, tcpconnection.TimeStampOpen, tcpconnection.TimeStampClose,
                    templates.LastOrDefault(), bytesSent, datagramsSent, bytesReceived, datagramsReceived, tcpconnection.ProcessIdx);
                ConnectionIdx connIdx = results.Network.TcpData.AddConnection(connection);
                connect2Idx[tcpconnection] = connIdx;


                // Detect client side retransmissions which can be identified by packets > 1 bytes 
                // and which have the same sequence number. 
                // Todo: Sequence rollover is currently handled by a 3s timeout filter which should work up to 10 GBit uplinks before
                // sequence rollover kicks in.
                foreach (var clientRetransmissions in receivedByConnection[tcpconnection].Where(x => x.NumBytes > 1).ToLookup(x => x.SequenceNr).Where(x => x.Count() > 1))
                {
                    var candidates = clientRetransmissions.OrderBy(x => x.Timestamp).ToArray();
                    DateTimeOffset first = candidates[0].Timestamp;
                    foreach (var retransCandidate in candidates.Skip(1))
                    {
                        TimeSpan diff = retransCandidate.Timestamp - first;
                        if (diff.TotalSeconds < 3)
                        {
                            var clientRetrans = new TcpRetransmission(connIdx, retransCandidate.Timestamp, first, retransCandidate.SequenceNr, retransCandidate.NumBytes, true);
                            results.Network.TcpData.Retransmissions.Add(clientRetrans);
                        }
                    }

                }

            }


            foreach (var retrans in myRetransmits)
            {
                retrans.Connection = LocateConnection(retrans.Tcb, retrans.Timestamp, results, ref connectionsByTcb);
                foreach (var sent in sentByConnection[retrans.Connection].OrderBy(x => x.Timestamp))
                {
                    if (sent.SequenceNr == retrans.SndUna)
                    {
                        results.Network.TcpData.Retransmissions.Add(new TcpRetransmission(connect2Idx[retrans.Connection], retrans.Timestamp, sent.Timestamp, sent.SequenceNr, sent.BytesSent));
                    }
                }
            }

        }


        readonly List<TcpTemplateChanged> myTemplateChangedEvents = new();

        private void OnTcpTemplateChanged(IGenericEvent ev)
        {
            TcpTemplateChanged changed = new(ev);
            myTemplateChangedEvents.Add(changed);
        }

        private void OnTcpDataTransferReceive(IGenericEvent ev)
        {
            TcpDataTransferReceive receive = new(ev);
            myReceiveEvents.Add(receive);
        }
  
        private void OnTcpAcceptListenerComplete(IGenericEvent ev)
        {
            TcpAcceptListenerComplete complete = new(ev);
            myAcceptListenerCompletes.Add(complete);
        }

        private void OnTailLossProbe(IGenericEvent ev)
        {
            TcpTailLossProbe probe = new(ev);
            TcpRetransmit resend = new(probe.Tcb, probe.SndUna, probe.Timestamp);
            myRetransmits.Add(resend);
        }

        readonly List<TcpConnectionRundown> myTcpConnectionRundowns = new();

        private void OnTcpConnectionRundown(IGenericEvent ev)
        {
            TcpConnectionRundown rundown = new(ev);
            myTcpConnectionRundowns.Add(rundown);
        }

        readonly List<TcpConnectionSummary> myTcpConnectionSummaries = new();

        private void OnTcpConnectionSummary(IGenericEvent ev)
        {
            TcpConnectionSummary summary = new(ev);
            myTcpConnectionSummaries.Add(summary);
        }

        private void OnClose(IGenericEvent ev)
        {
            ulong tcb =  (ulong) ev.Fields["Tcb"].AsAddress.Value;
            foreach(var connect in myConnections.OrderByDescending(x=>x.TimeStampOpen))
            {
                if( connect.Tcb == tcb )
                {
                    connect.TimeStampClose = ev.Timestamp.DateTimeOffset;
                    break;
                }
            }
        }

        private void OnConnect(IGenericEvent ev, ETWExtract extract)
        {
            ETWProcessIndex idx = extract.GetProcessIndexByPidAtTime(ev.Process.Id, ev.Timestamp.DateTimeOffset);
            TcpRequestConnect conn = new(ev, idx);
            myConnections.Add(conn);
        }

        private void OnRetransmit(IGenericEvent ev)
        {
            TcpRetransmit retrans = new(ev);
            myRetransmits.Add(retrans);
        }

        private void OnTpcDataTransferSend(IGenericEvent ev)
        {
            TcpDataSend sentPacket = new(ev);
            mySendEvents.Add(sentPacket);
        }

        private bool IsValidTcpEvent(IGenericEvent ev)
        {
            return ev.ProviderId == TcpETWConstants.Guid && ev.Process?.ImageName != null && ev.Fields != null;
        }

        TcpRequestConnect LocateConnection(ulong tcb, DateTimeOffset time, ETWExtract extract, ref ILookup<ulong, TcpRequestConnect> connectionsByTcb)
        {
            // Check if we have already a stored connection
            foreach (var connection in connectionsByTcb[tcb])
            {
                if (connection.IsMatching(tcb, time))
                {
                    return connection;
                }
            }

            // connection which did already exist before we did start are stored in TCP Rundown data
            foreach (var summary in myTcpConnectionRundowns)
            {
                // add existing connection 
                if (summary.Tcb == tcb)
                {
                    ETWProcessIndex processIdx = extract.GetProcessIndexByPidAtTime((int) summary.Pid, summary.Timestamp);
                    var connection = new TcpRequestConnect(summary.Tcb, summary.LocalIpAndPort, summary.RemoteIpAndPort, null, null, processIdx);
                    myConnections.Add(connection);
                    connectionsByTcb = myConnections.ToLookup(x => x.Tcb);
                    return connection;
                }
            }

            // Some connections are in connecting state but we have not got connect event anymore. Use This event instead
            foreach (var complete in myAcceptListenerCompletes)
            {
                if (complete.Tcb == tcb)
                {
                    var processIdx = extract.GetProcessIndexByPidAtTime((int) complete.ProcessId, complete.Timestamp);
                    var connection = new TcpRequestConnect(complete.Tcb, complete.LocalIpAndPort, complete.RemoteIpAndPort, complete.Timestamp, null, processIdx);
                    myConnections.Add(connection);
                    connectionsByTcb = myConnections.ToLookup(x => x.Tcb);
                    return connection;
                }
            }

            return null;
        }

    }
}
