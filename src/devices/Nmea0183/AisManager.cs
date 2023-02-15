﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Iot.Device.Common;
using Iot.Device.Nmea0183.Ais;
using Iot.Device.Nmea0183.AisSentences;
using Iot.Device.Nmea0183.Sentences;
using UnitsNet;
using NavigationStatus = Iot.Device.Nmea0183.Ais.NavigationStatus;

namespace Iot.Device.Nmea0183
{
    /// <summary>
    /// Interpreter for AIS messages from NMEA-0183 data streams.
    /// Accepts the encoded AIVDM and AIVDO sentences and converts them to user-understandable ship structures.
    /// </summary>
    /// <remarks>
    /// WARNING: Never rely on an AIS alarm as sole supervision of your surroundings! Many ships do not have AIS or the system may malfunction.
    /// Keep a lookout by eye and ear at all times!
    /// </remarks>
    public class AisManager : NmeaSinkAndSource
    {
        /// <summary>
        /// Time between repeats of the same warning. If this is set to a short value, the same proximity warning will be shown very often,
        /// which is typically annoying.
        /// </summary>
        public static readonly TimeSpan WarningRepeatTimeout = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Controls how often lost targets are removed completely from the target list. The timespan after which a target is considered lost
        /// is controlled via <see cref="Iot.Device.Nmea0183.Ais.TrackEstimationParameters.TargetLostTimeout"/>
        /// </summary>
        public static readonly TimeSpan CleanupLatency = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Delegate for AIS messages
        /// </summary>
        /// <param name="received">True if the message was received from another ship, false if the message is generated internally (e.g. a proximity warning)</param>
        /// <param name="sourceMmsi">Source MMSI</param>
        /// <param name="destinationMmsi">Destination MMSI. May be 0 for a broadcast message</param>
        /// <param name="text">The text of the message.</param>
        public delegate void AisMessageHandler(bool received, uint sourceMmsi, uint destinationMmsi, string text);

        private readonly bool _throwOnUnknownMessage;

        private AisParser _aisParser;

        /// <summary>
        /// We keep our own position cache, as we need to calculate CPA and TCPA values.
        /// </summary>
        private SentenceCache _cache;

        private ConcurrentDictionary<uint, AisTarget> _targets;

        private ConcurrentDictionary<string, (string Message, DateTimeOffset TimeStamp)> _activeWarnings;

        private object _lock;

        private DateTimeOffset? _lastCleanupCheck;

        /// <summary>
        /// This event fires when a new message (individual or broadcast) is received and also when the <see cref="AisManager"/> generates own messages.
        /// </summary>
        public event AisMessageHandler? OnMessage;

        private bool _aisAlarmsEnabled;

        private Thread? _aisBackgroundThread;

        private PositionProvider _positionProvider;

        /// <summary>
        /// Creates an instance of an <see cref="AisManager"/>
        /// </summary>
        /// <param name="interfaceName">Name of the manager, used for message routing</param>
        /// <param name="ownMmsi">The MMSI of the own ship</param>
        /// <param name="ownShipName">The name of the own ship</param>
        public AisManager(string interfaceName, uint ownMmsi, string ownShipName)
        : this(interfaceName, false, ownMmsi, ownShipName)
        {
        }

        /// <summary>
        /// Creates an instance of an <see cref="AisManager"/>
        /// </summary>
        /// <param name="interfaceName">Name of the manager, used for message routing</param>
        /// <param name="throwOnUnknownMessage">True if an exception should be thrown when parsing an unknown message type. This parameter
        /// is mainly intended for test scenarios where a data stream should be scanned for rare messages</param>
        /// <param name="ownMmsi">The MMSI of the own ship</param>
        /// <param name="ownShipName">The name of the own ship</param>
        public AisManager(string interfaceName, bool throwOnUnknownMessage, uint ownMmsi, string ownShipName)
            : base(interfaceName)
        {
            OwnMmsi = ownMmsi;
            OwnShipName = ownShipName;
            _throwOnUnknownMessage = throwOnUnknownMessage;
            _aisParser = new AisParser(throwOnUnknownMessage);
            _cache = new SentenceCache(this);
            _positionProvider = new PositionProvider(_cache);
            _targets = new ConcurrentDictionary<uint, AisTarget>();
            _lock = new object();
            _activeWarnings = new ConcurrentDictionary<string, (string Message, DateTimeOffset TimeStamp)>();
            AutoSendWarnings = true;
            _lastCleanupCheck = null;
            DeleteTargetAfterTimeout = TimeSpan.Zero;
            _aisAlarmsEnabled = false;
            TrackEstimationParameters = new TrackEstimationParameters();
        }

        /// <summary>
        /// The own MMSI
        /// </summary>
        public uint OwnMmsi { get; }

        /// <summary>
        /// The name of the own ship
        /// </summary>
        public string OwnShipName { get; }

        /// <summary>
        /// Distance from GPS receiver to bow of own ship, see <see cref="Ship.DimensionToBow"/>
        /// </summary>
        public Length DimensionToBow { get; set; }

        /// <summary>
        /// Distance from GPS receiver to stern of own ship, see <see cref="Ship.DimensionToStern"/>
        /// </summary>
        public Length DimensionToStern { get; set; }

        /// <summary>
        /// Distance from GPS receiver to Port of own ship, see <see cref="Ship.DimensionToPort"/>
        /// </summary>
        public Length DimensionToPort { get; set; }

        /// <summary>
        /// Distance from GPS receiver to Starboard of own ship, see <see cref="Ship.DimensionToStarboard"/>
        /// </summary>
        public Length DimensionToStarboard { get; set; }

        /// <summary>
        /// True to have the component automatically generate warning broadcast messages (when in collision range, or when seeing something unexpected,
        /// such as an AIS-Sart target)
        /// </summary>
        public bool AutoSendWarnings { get; set; }

        /// <summary>
        /// If a target has not been updated for this time, it is deleted from the list of targets.
        /// Additionally, client software should consider targets as lost whose <see cref="AisTarget.LastSeen"/> value is older than a minute or so.
        /// A value of 0 or less means infinite.
        /// </summary>
        public TimeSpan DeleteTargetAfterTimeout { get; set; }

        /// <summary>
        /// Set of parameters that control track estimation.
        /// </summary>
        public TrackEstimationParameters TrackEstimationParameters { get; private set; }

        /// <summary>
        /// Which <see cref="SentenceId"/> generated AIS messages should get. Meaningful values are <see cref="AisParser.VdmId"/> or <see cref="AisParser.VdoId"/>.
        /// Default is "VDO"
        /// </summary>
        public SentenceId GeneratedSentencesId
        {
            get
            {
                return _aisParser.GeneratedSentencesId;
            }
            set
            {
                _aisParser.GeneratedSentencesId = value;
            }
        }

        /// <summary>
        /// Gets the data of the own ship (including position and movement vectors) as a ship structure.
        /// </summary>
        /// <param name="ownShip">Receives the data about the own ship</param>
        /// <returns>True in case of success, false if relevant data is outdated or missing. Returns false if the
        /// last received position message is older than <see cref="TrackEstimationParameters.MaximumPositionAge"/>.</returns>
        public bool GetOwnShipData(out Ship ownShip)
        {
            return GetOwnShipData(out ownShip, DateTimeOffset.UtcNow);
        }

        /// <summary>
        /// Gets the data of the own ship (including position and movement vectors) as a ship structure.
        /// </summary>
        /// <param name="ownShip">Receives the data about the own ship</param>
        /// <param name="currentTime">The current time</param>
        /// <returns>True in case of success, false if relevant data is outdated or missing. Returns false if the
        /// last received position message is older than <see cref="TrackEstimationParameters.MaximumPositionAge"/>.</returns>
        public bool GetOwnShipData(out Ship ownShip, DateTimeOffset currentTime)
        {
            var s = new Ship(OwnMmsi);
            s.Name = OwnShipName;
            s.DimensionToBow = DimensionToBow;
            s.DimensionToStern = DimensionToStern;
            s.DimensionToPort = DimensionToPort;
            s.DimensionToStarboard = DimensionToStarboard;
            if (!_positionProvider.TryGetCurrentPosition(out var position, null, true, out var track, out var sog, out var heading,
                    out var messageTime, currentTime) || (messageTime + TrackEstimationParameters.MaximumPositionAge) < currentTime)
            {
                s.Position = position ?? new GeographicPosition();
                s.CourseOverGround = track;
                s.SpeedOverGround = sog;
                s.TrueHeading = heading;
                s.LastSeen = messageTime;
                ownShip = s;
                return false;
            }

            s.Position = position!;
            s.CourseOverGround = track;
            s.SpeedOverGround = sog;
            s.TrueHeading = heading;
            s.LastSeen = messageTime;

            ownShip = s;
            return true;
        }

        /// <inheritdoc />
        public override void StartDecode()
        {
        }

        /// <summary>
        /// Tries to retrieve the target with the given MMSI from the database
        /// </summary>
        /// <param name="mmsi">MMSI to query</param>
        /// <param name="target">Returns the given target, if found. The target should be cast to a more concrete type</param>
        /// <returns>True if the target was found, false otherwise</returns>
        public bool TryGetTarget(uint mmsi,
#if NET5_0_OR_GREATER
            [NotNullWhen(true)]
#endif
            out AisTarget target)
        {
            lock (_lock)
            {
                AisTarget? tgt;
                bool ret = _targets.TryGetValue(mmsi, out tgt);
                target = tgt!;
                return ret;
            }
        }

        private Ship GetOrCreateShip(uint mmsi, AisTransceiverClass transceiverClass, DateTimeOffset? lastSeenTime)
        {
            lock (_lock)
            {
                var ship = GetOrCreateTarget<Ship>(mmsi, x => new Ship(x), lastSeenTime);

                // The transceiver type is derived from the message type (a PositionReportClassA message is obviously only sent by class A equipment)
                if (transceiverClass != AisTransceiverClass.Unknown)
                {
                    ship.TransceiverClass = transceiverClass;
                }

                return ship!;
            }
        }

        private T GetOrCreateTarget<T>(uint mmsi, Func<uint, T> constructor, DateTimeOffset? lastSeenTime)
        where T : AisTarget
        {
            lock (_lock)
            {
                AisTarget? target;
                T? ship;
                if (TryGetTarget(mmsi, out target) && target is Ship)
                {
                    ship = target as T;
                }
                else
                {
                    // Remove the existing key (this is for the rare case where the same MMSI suddenly changes type from ship to base station or similar.
                    // That should not normally happen, but we need to be robust about it.
                    _targets.TryRemove(mmsi, out _);
                    ship = constructor(mmsi);
                    _targets.TryAdd(mmsi, ship);
                }

                if (lastSeenTime.HasValue && ship != null)
                {
                    ship.LastSeen = lastSeenTime.Value;
                }

                return ship!;
            }
        }

        private BaseStation GetOrCreateBaseStation(uint mmsi, AisTransceiverClass transceiverClass, DateTimeOffset? lastSeenTime)
        {
            return GetOrCreateTarget<BaseStation>(mmsi, x => new BaseStation(mmsi), lastSeenTime);
        }

        private SarAircraft GetOrCreateSarAircraft(uint mmsi, DateTimeOffset? lastSeenTime)
        {
            return GetOrCreateTarget<SarAircraft>(mmsi, x => new SarAircraft(mmsi), lastSeenTime);
        }

        /// <summary>
        /// Gets the list of active targets
        /// </summary>
        /// <returns>An enumeration of all currently tracked targets</returns>
        public IEnumerable<AisTarget> GetTargets()
        {
            lock (_lock)
            {
                return _targets.Values;
            }
        }

        /// <summary>
        /// Gets the list of all active targets of the given type
        /// </summary>
        /// <typeparam name="T">A type of target, must be a derivative of <see cref="AisTarget"/>.</typeparam>
        /// <returns>An enumeration of all targets of that type</returns>
        public IEnumerable<T> GetSpecificTargets<T>()
        where T : AisTarget
        {
            lock (_lock)
            {
                return _targets.Values.OfType<T>();
            }
        }

        /// <summary>
        /// Processes incomming sequences. Use this method to input an NMEA stream to this component.
        /// Note that _all_ messages should be forwarded to this method, as AIS target tracking requires the position and speed of our own vessel.
        /// </summary>
        /// <param name="source">Message source</param>
        /// <param name="sentence">The new sentence</param>
        public override void SendSentence(NmeaSinkAndSource source, NmeaSentence sentence)
        {
            _cache.Add(sentence);

            DoCleanup(sentence.DateTime);

            AisMessage? msg = _aisParser.Parse(sentence);
            if (msg == null)
            {
                return;
            }

            Ship? ship;
            lock (_lock)
            {
                switch (msg.MessageType)
                {
                    // These contain the same data
                    case AisMessageType.PositionReportClassA:
                    case AisMessageType.PositionReportClassAAssignedSchedule:
                    case AisMessageType.PositionReportClassAResponseToInterrogation:
                    {
                        PositionReportClassAMessageBase msgPos = (PositionReportClassAMessageBase)msg;
                        ship = GetOrCreateShip(msgPos.Mmsi, msg.TransceiverType, sentence.DateTime);
                        PositionReportClassAToShip(ship, msgPos);

                        CheckIsExceptionalTarget(ship, sentence.DateTime);
                        break;
                    }

                    case AisMessageType.StaticDataReport:
                    {
                        // This is the normal static data report from class B transceivers
                        ship = GetOrCreateShip(msg.Mmsi, msg.TransceiverType, null);
                        if (msg is StaticDataReportPartAMessage msgPartA)
                        {
                            ship.Name = msgPartA.ShipName;
                        }
                        else if (msg is StaticDataReportPartBMessage msgPartB)
                        {
                            ship.CallSign = msgPartB.CallSign;
                            ship.ShipType = msgPartB.ShipType;
                            ship.DimensionToBow = Length.FromMeters(msgPartB.DimensionToBow);
                            ship.DimensionToStern = Length.FromMeters(msgPartB.DimensionToStern);
                            ship.DimensionToPort = Length.FromMeters(msgPartB.DimensionToPort);
                            ship.DimensionToStarboard = Length.FromMeters(msgPartB.DimensionToStarboard);
                        }

                        CheckIsExceptionalTarget(ship, sentence.DateTime);
                        break;
                    }

                    case AisMessageType.StaticAndVoyageRelatedData:
                    {
                        // This message is only sent by class A transceivers.
                        ship = GetOrCreateShip(msg.Mmsi, msg.TransceiverType, null);
                        StaticAndVoyageRelatedDataMessage voyage = (StaticAndVoyageRelatedDataMessage)msg;
                        ship.Name = voyage.ShipName;
                        ship.CallSign = voyage.CallSign;
                        ship.Destination = voyage.Destination;
                        ship.Draught = Length.FromMeters(voyage.Draught);
                        ship.ImoNumber = voyage.ImoNumber;
                        ship.ShipType = voyage.ShipType;
                        var now = DateTimeOffset.UtcNow;
                        if (voyage.IsEtaValid())
                        {
                            int year = now.Year;
                            // If we are supposed to arrive on a month less than the current, this probably means "next year".
                            if (voyage.EtaMonth < now.Month ||
                                (voyage.EtaMonth == now.Month && voyage.EtaDay < now.Day))
                            {
                                year += 1;
                            }

                            try
                            {
                                ship.EstimatedTimeOfArrival = new DateTimeOffset(year, (int)voyage.EtaMonth,
                                    (int)voyage.EtaDay,
                                    (int)voyage.EtaHour, (int)voyage.EtaMinute, 0, TimeSpan.Zero);
                            }
                            catch (Exception x) when (x is ArgumentException || x is ArgumentOutOfRangeException)
                            {
                                // Even when the simple validation above succeeds, the date may still be illegal (e.g. 31 February)
                                ship.EstimatedTimeOfArrival = null;
                            }
                        }
                        else
                        {
                            ship.EstimatedTimeOfArrival = null; // may be deleted by the user
                        }

                        CheckIsExceptionalTarget(ship, sentence.DateTime);
                        break;
                    }

                    case AisMessageType.StandardClassBCsPositionReport:
                    {
                        // This is an alternative static data report for class B transceivers
                        StandardClassBCsPositionReportMessage msgPos = (StandardClassBCsPositionReportMessage)msg;
                        ship = GetOrCreateShip(msgPos.Mmsi, msg.TransceiverType, sentence.DateTime);
                        ship.Position = new GeographicPosition(msgPos.Latitude, msgPos.Longitude, 0);
                        ship.RateOfTurn = null;
                        if (msgPos.TrueHeading.HasValue)
                        {
                            ship.TrueHeading = Angle.FromDegrees(msgPos.TrueHeading.Value);
                        }
                        else
                        {
                            ship.TrueHeading = null;
                        }

                        ship.CourseOverGround = Angle.FromDegrees(msgPos.CourseOverGround);
                        ship.SpeedOverGround = Speed.FromKnots(msgPos.SpeedOverGround);
                        CheckIsExceptionalTarget(ship, sentence.DateTime);
                        break;
                    }

                    case AisMessageType.ExtendedClassBCsPositionReport:
                    {
                        ExtendedClassBCsPositionReportMessage msgPos = (ExtendedClassBCsPositionReportMessage)msg;
                        ship = GetOrCreateShip(msgPos.Mmsi, msg.TransceiverType, sentence.DateTime);
                        ship.Position = new GeographicPosition(msgPos.Latitude, msgPos.Longitude, 0);
                        ship.RateOfTurn = null;
                        if (msgPos.TrueHeading.HasValue)
                        {
                            ship.TrueHeading = Angle.FromDegrees(msgPos.TrueHeading.Value);
                        }
                        else
                        {
                            ship.TrueHeading = null;
                        }

                        ship.CourseOverGround = Angle.FromDegrees(msgPos.CourseOverGround);
                        ship.SpeedOverGround = Speed.FromKnots(msgPos.SpeedOverGround);
                        ship.DimensionToBow = Length.FromMeters(msgPos.DimensionToBow);
                        ship.DimensionToStern = Length.FromMeters(msgPos.DimensionToStern);
                        ship.DimensionToPort = Length.FromMeters(msgPos.DimensionToPort);
                        ship.DimensionToStarboard = Length.FromMeters(msgPos.DimensionToStarboard);
                        ship.ShipType = msgPos.ShipType;
                        ship.Name = msgPos.Name;
                        CheckIsExceptionalTarget(ship, sentence.DateTime);
                        break;
                    }

                    case AisMessageType.BaseStationReport:
                    {
                        BaseStationReportMessage rpt = (BaseStationReportMessage)msg;
                        var station = GetOrCreateBaseStation(rpt.Mmsi, rpt.TransceiverType, sentence.DateTime);
                        station.Position = new GeographicPosition(rpt.Latitude, rpt.Longitude, 0);
                        break;
                    }

                    case AisMessageType.StandardSarAircraftPositionReport:
                    {
                        StandardSarAircraftPositionReportMessage sar = (StandardSarAircraftPositionReportMessage)msg;
                        var sarAircraft = GetOrCreateSarAircraft(sar.Mmsi, sentence.DateTime);
                        // Is the altitude here ellipsoid or geoid? Ships are normally at 0m geoid (unless on a lake, but the AIS system doesn't seem to be designed
                        // for that)
                        sarAircraft.Position = new GeographicPosition(sar.Latitude, sar.Longitude, sar.Altitude);
                        sarAircraft.CourseOverGround = Angle.FromDegrees(sar.CourseOverGround);
                        sarAircraft.SpeedOverGround = Speed.FromKnots(sar.SpeedOverGround);
                        sarAircraft.RateOfTurn = RotationalSpeed.Zero;
                        break;
                    }

                    case AisMessageType.AidToNavigationReport:
                    {
                        AidToNavigationReportMessage aton = (AidToNavigationReportMessage)msg;
                        var navigationTarget = GetOrCreateTarget(aton.Mmsi, x => new AidToNavigation(x), sentence.DateTime);
                        navigationTarget.Position = new GeographicPosition(aton.Latitude, aton.Longitude, 0);
                        navigationTarget.Name = aton.Name + aton.NameExtension;
                        navigationTarget.DimensionToBow = Length.FromMeters(aton.DimensionToBow);
                        navigationTarget.DimensionToStern = Length.FromMeters(aton.DimensionToStern);
                        navigationTarget.DimensionToPort = Length.FromMeters(aton.DimensionToPort);
                        navigationTarget.DimensionToStarboard = Length.FromMeters(aton.DimensionToStarboard);
                        navigationTarget.OffPosition = aton.OffPosition;
                        navigationTarget.Virtual = aton.VirtualAid;
                        navigationTarget.NavigationalAidType = aton.NavigationalAidType;
                        break;
                    }

                    case AisMessageType.Interrogation:
                    {
                        // Currently nothing to do with these
                        InterrogationMessage interrogation = (InterrogationMessage)msg;
                        break;
                    }

                    case AisMessageType.DataLinkManagement:
                        // not interesting.
                        break;

                    case AisMessageType.AddressedSafetyRelatedMessage:
                    {
                        AddressedSafetyRelatedMessage addressedSafetyRelatedMessage = (AddressedSafetyRelatedMessage)msg;
                        OnMessage?.Invoke(true, addressedSafetyRelatedMessage.Mmsi, addressedSafetyRelatedMessage.DestinationMmsi, addressedSafetyRelatedMessage.Text);
                        break;
                    }

                    case AisMessageType.SafetyRelatedBroadcastMessage:
                    {
                        SafetyRelatedBroadcastMessage broadcastMessage = (SafetyRelatedBroadcastMessage)msg;
                        OnMessage?.Invoke(true, broadcastMessage.Mmsi, 0, broadcastMessage.Text);
                        break;
                    }

                    default:
                        if (_throwOnUnknownMessage)
                        {
                            throw new NotSupportedException($"Received a message of type {msg.MessageType} which was not handled");
                        }

                        break;
                }
            }
        }

        internal void PositionReportClassAToShip(Ship ship, PositionReportClassAMessageBase positionReport)
        {
            ship.Position = new GeographicPosition(positionReport.Latitude, positionReport.Longitude, 0);
            if (positionReport.RateOfTurn.HasValue)
            {
                // See the cheat sheet at https://gpsd.gitlab.io/gpsd/AIVDM.html
                double v = positionReport.RateOfTurn.Value / 4.733;
                ship.RateOfTurn = RotationalSpeed.FromDegreesPerMinute(Math.Sign(v) * v * v); // Square value, keep sign
            }
            else
            {
                ship.RateOfTurn = null;
            }

            if (positionReport.TrueHeading.HasValue)
            {
                ship.TrueHeading = Angle.FromDegrees(positionReport.TrueHeading.Value);
            }
            else
            {
                ship.TrueHeading = null;
            }

            ship.CourseOverGround = Angle.FromDegrees(positionReport.CourseOverGround);
            ship.SpeedOverGround = Speed.FromKnots(positionReport.SpeedOverGround);
            ship.NavigationStatus = positionReport.NavigationStatus;
        }

        private void CheckIsExceptionalTarget(Ship ship, DateTimeOffset now)
        {
            void SendMessage(Ship ship, string type)
            {
                GetOwnShipData(out Ship ownShip); // take in in either case
                Length distance = ownShip.DistanceTo(ship);
                SendWarningMessage(ship.FormatMmsi(), ship.Mmsi,
                    $"{type} Target activated: MMSI {ship.Mmsi} in Position {ship.Position:M1N M1E}! Distance {distance}", now);
            }

            if (AutoSendWarnings == false)
            {
                return;
            }

            if (ship.NavigationStatus == NavigationStatus.AisSartIsActive)
            {
                SendMessage(ship, "AIS SART status");
            }

            MmsiType type = ship.IdentifyMmsiType();
            switch (type)
            {
                case MmsiType.AisSart:
                    SendMessage(ship, "AIS SART");
                    break;
                case MmsiType.Epirb:
                    SendMessage(ship, "EPIRB");
                    break;
                case MmsiType.Mob:
                    SendMessage(ship, "AIS MOB");
                    break;
            }
        }

        /// <summary>
        /// Sends a message with the given <paramref name="messageText"/> as an AIS broadcast message
        /// </summary>
        /// <param name="messageId">Identifies the message. Messages with the same ID are only sent once, until the timeout elapses</param>
        /// <param name="sourceMmsi">Source MMSI, can be 0 if irrelevant/unknown</param>
        /// <param name="messageText">The text of the message. Supports only the AIS 6-bit character set.</param>
        /// <returns>True if the message was sent, false otherwise</returns>
        public bool SendWarningMessage(string messageId, uint sourceMmsi, string messageText)
        {
            return SendWarningMessage(messageId, sourceMmsi, messageText, DateTimeOffset.UtcNow);
        }

        /// <summary>
        /// Sends a message with the given <paramref name="messageText"/> as an AIS broadcast message
        /// </summary>
        /// <param name="messageId">Identifies the message. Messages with the same ID are only sent once, until the timeout elapses</param>
        /// <param name="sourceMmsi">Source MMSI, can be 0 if irrelevant/unknown</param>
        /// <param name="messageText">The text of the message. Supports only the AIS 6-bit character set.</param>
        /// <param name="now">The current time (to verify the timeout against)</param>
        /// <returns>True if the message was sent, false otherwise</returns>
        public bool SendWarningMessage(string messageId, uint sourceMmsi, string messageText, DateTimeOffset now)
        {
            if (_activeWarnings.TryGetValue(messageId, out var msg))
            {
                if (msg.TimeStamp + WarningRepeatTimeout > now)
                {
                    return false;
                }

                _activeWarnings.TryRemove(messageId, out _);
            }

            if (_activeWarnings.TryAdd(messageId, (messageText, now)))
            {
                SendBroadcastMessage(sourceMmsi, messageText);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Send an AIS broadcast message to the NMEA stream (output!)
        /// Some NMEA devices (in particular general-purpose displays) may pick up this information
        /// from the data stream and show the warning to the user.
        /// </summary>
        /// <param name="sourceMmsi">The message source, can be 0</param>
        /// <param name="text">The text. Will be converted to 6-Bit-Ascii (e.g. only capital letters)</param>
        public void SendBroadcastMessage(uint sourceMmsi, string text)
        {
            SafetyRelatedBroadcastMessage msg = new SafetyRelatedBroadcastMessage();
            msg.Mmsi = sourceMmsi;
            msg.Text = text;
            OnMessage?.Invoke(false, sourceMmsi, 0, text);
            List<NmeaSentence> sentences = _aisParser.ToSentences(msg);
            foreach (var s in sentences)
            {
                DispatchSentenceEvents(this, s);
            }
        }

        /// <inheritdoc />
        public override void StopDecode()
        {
            EnableAisAlarms(false, null);
            _activeWarnings.Clear();
        }

        internal PositionReportClassAMessage ShipToPositionReportClassAMessage(Ship ship)
        {
            PositionReportClassAMessage rpt = new PositionReportClassAMessage();
            rpt.Mmsi = ship.Mmsi;
            rpt.SpeedOverGround = ship.SpeedOverGround.Knots;
            if (ship.RateOfTurn.HasValue)
            {
                // Inverse of the formula above
                double v = ship.RateOfTurn.Value.DegreesPerMinute;
                v = Math.Sign(v) * Math.Sqrt(Math.Abs(v));
                v = v * 4.733;
                rpt.RateOfTurn = (int)Math.Round(v);
            }
            else
            {
                rpt.RateOfTurn = null;
            }

            rpt.CourseOverGround = ship.CourseOverGround.Degrees;
            rpt.Latitude = ship.Position.Latitude;
            rpt.Longitude = ship.Position.Longitude;
            rpt.ManeuverIndicator = ManeuverIndicator.NoSpecialManeuver;
            rpt.NavigationStatus = ship.NavigationStatus;
            if (ship.TrueHeading.HasValue)
            {
                rpt.TrueHeading = (uint)ship.TrueHeading.Value.Degrees;
            }

            return rpt;
        }

        /// <summary>
        /// Sends a ship position report for the given ship to the NMEA stream. Useful for testing or simulation.
        /// </summary>
        /// <param name="transceiverClass">Transceiver class to simulate</param>
        /// <param name="ship">The ship whose position data to send</param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException">An internal inconsistency occurred</exception>
        /// <exception cref="NotSupportedException">This message type is not currently supported for encoding</exception>
        public NmeaSentence SendShipPositionReport(AisTransceiverClass transceiverClass, Ship ship)
        {
            if (transceiverClass == AisTransceiverClass.A)
            {
                PositionReportClassAMessage msg = ShipToPositionReportClassAMessage(ship);
                List<NmeaSentence> sentences = _aisParser.ToSentences(msg);
                if (sentences.Count != 1)
                {
                    throw new InvalidOperationException(
                        $"Encoding the position report for class A returned {sentences.Count} sentences. Exactly 1 expected");
                }

                NmeaSentence single = sentences.Single();

                DispatchSentenceEvents(this, single);
                return single;
            }
            else
            {
                throw new NotSupportedException("Only class A messages can currently be constructed");
            }
        }

        /// <summary>
        /// Regularly scan our database to check for outdated targets. This is done from
        /// the parser thread, so we don't need to create a separate thread just for this.
        /// </summary>
        /// <param name="currentTime">The time of the last packet</param>
        private void DoCleanup(DateTimeOffset currentTime)
        {
            if (DeleteTargetAfterTimeout <= TimeSpan.Zero)
            {
                return;
            }

            // Do if the cleanuplatency has elapsed
            if (_lastCleanupCheck == null || _lastCleanupCheck.Value + CleanupLatency < currentTime)
            {
                lock (_lock)
                {
                    foreach (var t in _targets.Values)
                    {
                        if (t.Age(currentTime) > DeleteTargetAfterTimeout)
                        {
                            _targets.TryRemove(t.Mmsi, out _);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Gets the target with the given MMSI
        /// </summary>
        /// <param name="mmsi">The MMSI to search</param>
        /// <returns>The given target or null if it was not found.</returns>
        public AisTarget? GetTarget(uint mmsi)
        {
            lock (_lock)
            {
                return _targets.Values.FirstOrDefault(x => x.Mmsi == mmsi);
            }
        }

        /// <summary>
        /// Enable automatic generation of AIS alarms.
        /// This method will start a background thread that regularly evaluates all ships in vicinity for possibly dangerous proximity.
        /// It uses an estimate of a track for each ship to find the closest point of approach (CPA) and the time to that closest point (TCPA).
        /// When this is enabled, <see cref="AisTarget.RelativePosition"/> will be regularly updated for all targets.
        /// </summary>
        /// <param name="enable">True to enable AIS alarms. The alarms will be presented by a message on the outgoing stream and a call to <see cref="OnMessage"/></param>
        /// <param name="parameters">Parameter set to use for the estimation</param>
        /// <remarks>Note 1: Since this uses a precise track estimation that includes COG change, the calculation is rather expensive. CPU
        /// performance should be monitored when in a crowded area. Algorithm improvements that cut CPU usage e.g. for stationary ships are pending.
        /// Note 2: The algorithm is experimental and should not be relied on.
        /// Also read the notes at <see cref="AisManager"/>
        /// </remarks>
        public void EnableAisAlarms(bool enable, TrackEstimationParameters? parameters = null)
        {
            _aisAlarmsEnabled = enable;
            if (parameters != null)
            {
                TrackEstimationParameters = parameters;
            }

            if (enable)
            {
                var t = _aisBackgroundThread;
                if (t != null && t.IsAlive)
                {
                    return;
                }

                t = new Thread(AisAlarmThread);
                t.Start();
                _aisBackgroundThread = t;
            }
            else
            {
                var t = _aisBackgroundThread;
                if (t != null)
                {
                    t.Join();
                    _aisBackgroundThread = null;
                }
            }
        }

        /// <summary>
        /// Clears the list of suppressed warnings
        /// </summary>
        public void ClearWarnings()
        {
            _activeWarnings.Clear();
        }

        /// <summary>
        /// This operation may be very expensive, so we need to do it in its own thread
        /// </summary>
        private void AisAlarmThread()
        {
            AisAlarmThread(DateTimeOffset.UtcNow);
        }

        /// <summary>
        /// This operation may be very expensive, so we need to do it in its own thread
        /// </summary>
        /// <param name="time">The current time (used mainly for testing)</param>
        internal void AisAlarmThread(DateTimeOffset time)
        {
            Stopwatch sw = new Stopwatch();
            // This uses a do-while for easier testability
            do
            {
                Ship ownShip;
                if (GetOwnShipData(out ownShip, time) == false)
                {
                    if (TrackEstimationParameters.WarnIfGnssMissing)
                    {
                        if (ownShip.Position.ContainsValidPosition())
                        {
                            // Data is valid, but old
                            SendWarningMessage("GNSSOLD", ownShip.Mmsi, "GNSS fix lost. No current position");
                        }
                        else
                        {
                            SendWarningMessage("NOGNSS", ownShip.Mmsi, "No GNSS data");
                        }
                    }

                    Thread.Sleep(TrackEstimationParameters.AisSafetyCheckInterval);
                    goto nextloop;
                }

                sw.Restart();

                // it's a ConcurrentDictionary, so iterating over it without a lock is fine
                List<ShipRelativePosition> differences = ownShip.RelativePositionsTo(_targets.Values, time, TrackEstimationParameters);

                foreach (var difference in differences)
                {
                    var timeToClosest = difference.TimeToClosestPointOfApproach(time);
                    if (difference.ClosestPointOfApproach < TrackEstimationParameters.WarningDistance &&
                        timeToClosest > -TimeSpan.FromMinutes(1) && timeToClosest < TrackEstimationParameters.WarningTime)
                    {
                        // Warn if the ship will be closer than the warning distance in less than the WarningTime
                        string name = difference.To.Name ?? difference.To.FormatMmsi();
                        SendWarningMessage("DANGEROUS VESSEL-" + difference.To.Mmsi, difference.To.Mmsi, $"{name} is dangerously close. CPA {difference.ClosestPointOfApproach}; TCPA {timeToClosest:mm\\:ss}", time);
                    }
                }

                lock (_lock)
                {
                    // Separate loop, because this one requires a lock and is cheaper than the above (sending a message may be expensive and could potentially be recursive)
                    foreach (var difference in differences)
                    {
                        // Good we keep the target ship in the type, otherwise this would require an O(n^2) iteration
                        difference.To.RelativePosition = difference;
                    }
                }

                nextloop:
                // Restart the loop every check interval if the current interval used less time than allocated.
                // We always wait at least 20ms, so that we don't fully block the CPU (even thought that could be really a small amount)
                if (TrackEstimationParameters.AisSafetyCheckInterval > TimeSpan.Zero)
                {
                    TimeSpan remaining = TrackEstimationParameters.AisSafetyCheckInterval - sw.Elapsed;
                    if (remaining < TimeSpan.FromMilliseconds(20))
                    {
                        remaining = TimeSpan.FromMilliseconds(20);
                    }

                    Thread.Sleep(remaining);
                }
            }
            while (_aisAlarmsEnabled);
        }
    }
}
