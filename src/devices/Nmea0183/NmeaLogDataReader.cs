﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Iot.Device.Common;
using Iot.Device.Nmea0183.Sentences;

namespace Iot.Device.Nmea0183
{
    /// <summary>
    /// This source can be used to play back a recorded log file.
    /// If <see cref="DecodeInRealtime"/> is false (the default), the file will be read as fast as possible. Otherwise messages
    /// will be generated at the speed of the original data.
    /// </summary>
    public class NmeaLogDataReader : NmeaSinkAndSource
    {
        private readonly IEnumerable<(string Name, Stream? Alternate)> _filesToRead;
        private DateTimeOffset? _referenceTimeInLog;
        private DateTimeOffset? _referenceTimeNow;
        private NmeaParser? _internalParser;
        private ManualResetEvent? _doneEvent;

        /// <summary>
        /// Reads a log file and uses it as a source
        /// </summary>
        /// <param name="interfaceName">Name of this interface</param>
        /// <param name="filesToRead">Files to read. Either a | delimited log or a plain text file</param>
        public NmeaLogDataReader(string interfaceName, IEnumerable<string> filesToRead)
            : base(interfaceName)
        {
            _filesToRead = filesToRead.Select<string, (string, Stream?)>(x => (x, null));
            _doneEvent = new ManualResetEvent(false);
        }

        /// <summary>
        /// Reads a log file and uses it as a source
        /// </summary>
        /// <param name="interfaceName">Name of this interface</param>
        /// <param name="fileToRead">File to read. Either a | delimited log or a plain text file</param>
        public NmeaLogDataReader(string interfaceName, string fileToRead)
            : base(interfaceName)
        {
            _filesToRead = new List<(string Name, Stream? Alternate)>()
            {
                (fileToRead, null)
            };

            _doneEvent = new ManualResetEvent(false);
        }

        /// <summary>
        /// Reads a log file and uses it as a source
        /// </summary>
        /// <param name="interfaceName">Name of this interface</param>
        /// <param name="streamToRead">A file stream to read. Either a | delimited log or a plain text file</param>
        public NmeaLogDataReader(string interfaceName, Stream streamToRead)
            : base(interfaceName)
        {
            _filesToRead = new List<(string Name, Stream? Alternate)>()
            {
                (string.Empty, streamToRead)
            };

            _doneEvent = new ManualResetEvent(false);
        }

        /// <summary>
        /// Reads a log file and uses it as a source
        /// </summary>
        /// <param name="interfaceName">Name of this interface</param>
        /// <param name="streamsToRead">A file stream to read. Either a | delimited log or a plain text file</param>
        public NmeaLogDataReader(string interfaceName, IEnumerable<Stream> streamsToRead)
            : base(interfaceName)
        {
            if (streamsToRead.Any(x => x == null))
            {
                throw new ArgumentNullException(nameof(streamsToRead), "Must not provide null streams");
            }

            _filesToRead = streamsToRead.Select<Stream, (string, Stream?)>(x => (string.Empty, x)).ToList();

            _doneEvent = new ManualResetEvent(false);
        }

        /// <summary>
        /// Set to true to replay a message stream in real time. The reader will generate
        /// messages with the same time difference as they originally had. The timestamp of the
        /// messages will be updated to the present.
        /// </summary>
        public bool DecodeInRealtime
        {
            get;
            set;
        }

        /// <inheritdoc />
        public override void StartDecode()
        {
            var ms = new FileSetStream(_filesToRead);
            _doneEvent = new ManualResetEvent(false);
            _internalParser = new NmeaParser(InterfaceName, ms, null);
            _internalParser.SupportLogReading = true;
            _internalParser.SuppressOutdatedMessages = false; // parse all incoming messages, ignoring any timing
            if (DecodeInRealtime)
            {
                _internalParser.OnNewSequence += ForwardDecodedRealTime;
            }
            else
            {
                _internalParser.OnNewSequence += ForwardDecoded;
            }

            _internalParser.OnParserError += (source, s, error) =>
            {
                if (error == NmeaError.PortClosed)
                {
                    _doneEvent.Set();
                }
            };
            _internalParser.StartDecode();
        }

        private void ForwardDecoded(NmeaSinkAndSource source, NmeaSentence sentence)
        {
            DispatchSentenceEvents(source, sentence);
        }

        private void ForwardDecodedRealTime(NmeaSinkAndSource source, NmeaSentence sentence)
        {
            var now = DateTimeOffset.UtcNow;
            if (_referenceTimeInLog == null)
            {
                if (sentence.SentenceId != TimeDate.Id || sentence.Valid == false)
                {
                    // We need a GPZDA sentence to start
                    return;
                }

                _referenceTimeInLog = sentence.DateTime;
                _referenceTimeNow = now;
            }

            // var timeThatHasPassedNow = DateTimeOffset.UtcNow - _referenceTimeNow;
            var timeThatHasPassedInLog = sentence.DateTime - _referenceTimeInLog;
            DateTimeOffset? timeMessageNeedsToBeSent = _referenceTimeNow + timeThatHasPassedInLog;

            // This is positive if the message shall be sent in the future.
            TimeSpan? waitTime = timeMessageNeedsToBeSent - now;
            if (waitTime > TimeSpan.Zero)
            {
                // Just block until the time is reached.
                Thread.Sleep(waitTime.Value);
            }

            sentence.DateTime = now;

            DispatchSentenceEvents(source, sentence);
        }

        /// <inheritdoc />
        public override void SendSentence(NmeaSinkAndSource source, NmeaSentence sentence)
        {
        }

        /// <inheritdoc />
        public override void StopDecode()
        {
            // If this is false, wait for the end of the file
            if (!DecodeInRealtime)
            {
                _doneEvent?.WaitOne(); // Wait for end of file
            }

            _internalParser?.StopDecode();
            _internalParser?.Dispose();
            _internalParser = null;
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            // Don't wait any more
            if (_doneEvent != null)
            {
                _doneEvent.Dispose();
                _doneEvent = null;
            }

            if (disposing)
            {
                StopDecode();
            }

            base.Dispose(disposing);
        }
    }
}
