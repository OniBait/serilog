﻿// Copyright 2013 Serilog Contributors
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.IO;
using System.Linq;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Sinks.IOFile;

namespace Serilog.Sinks.RollingFile
{
    /// <summary>
    /// Date-based rolling only is supported.
    /// </summary>
    sealed class RollingFileSink : ILogEventSink, IDisposable
    {
        readonly TemplatedPathRoller _roller;
        readonly ITextFormatter _textFormatter;
        readonly long? _fileSizeLimitBytes;
        readonly int? _retainedFileCountLimit;
        readonly object _syncRoot = new object();

        bool _isDisposed;
        DateTime? _nextCheckpoint;
        FileSink _currentFile;

        public RollingFileSink(string pathTemplate, 
                              ITextFormatter textFormatter,
                              long? fileSizeLimitBytes,
                              int? retainedFileCountLimit)
        {
            if (pathTemplate == null) throw new ArgumentNullException("pathTemplate");
            if (fileSizeLimitBytes.HasValue && fileSizeLimitBytes < 0) throw new ArgumentException("Negative value provided; file size limit must be non-negative");
            if (retainedFileCountLimit.HasValue && retainedFileCountLimit < 1) throw new ArgumentException("Zero or negative value provided; retained file count limit must be at least 1");
            
            _roller = new TemplatedPathRoller(pathTemplate);
            _textFormatter = textFormatter;
            _fileSizeLimitBytes = fileSizeLimitBytes;
            _retainedFileCountLimit = retainedFileCountLimit;
        }

        // Simplifications:
        // Events that come in out-of-order (e.g. around the rollovers)
        // may end up written to a later file than their timestamp
        // would indicate. 
        public void Emit(LogEvent logEvent)
        {
            if (logEvent == null) throw new ArgumentNullException("logEvent");

            lock (_syncRoot)
            {
                if (_isDisposed) throw new ObjectDisposedException("The rolling file has been disposed.");

                AlignCurrentFileTo(Clock.DateTimeNow);
                _currentFile.Emit(logEvent);
            }
        }

        void AlignCurrentFileTo(DateTime now)
        {
            if (!_nextCheckpoint.HasValue)
            {
                OpenFile(now);
            }
            else if (now >= _nextCheckpoint.Value)
            {
                CloseFile();
                OpenFile(now);
            }
        }

        void OpenFile(DateTime now)
        {
            string path;
            DateTime nextCheckpoint;
            _roller.GetLogFilePath(now, out path, out nextCheckpoint);
            _nextCheckpoint = nextCheckpoint;            
            _currentFile = new FileSink(path, _textFormatter, _fileSizeLimitBytes);
            ApplyRetentionPolicy(path);
        }

        void ApplyRetentionPolicy(string currentFilePath)
        {
            if (_retainedFileCountLimit == null) return;
            
            var currentFileName = Path.GetFileName(currentFilePath);

            // We consider the current file to exist, even if nothing's been written yet,
            // because files are only opened on response to an event being processed.
            var potentialMatches = Directory.GetFiles(_roller.LogFileDirectory, _roller.DirectorySearchPattern)
                .Union(new [] { currentFileName })
                .Select(Path.GetFileName);

            var newestFirst = _roller.OrderMatchingByAge(potentialMatches);
            var toRemove = newestFirst
                .Where(n => StringComparer.OrdinalIgnoreCase.Compare(currentFileName, n) != 0)
                .Skip(_retainedFileCountLimit.Value - 1)
                .ToList();

            foreach (var obsolete in toRemove)
            {
                var fullPath = Path.Combine(_roller.LogFileDirectory, obsolete);
                try
                {
                    File.Delete(fullPath);
                }
                catch (Exception ex)
                {
                    SelfLog.WriteLine("Error {0} while removing obsolete file {1}", ex, fullPath);
                }
            }
        }

        public void Dispose()
        {
            lock (_syncRoot)
            {
                if (_currentFile == null) return;
                CloseFile();
                _isDisposed = true;
            }
        }

        void CloseFile()
        {
            _currentFile.Dispose();
            _currentFile = null;
            _nextCheckpoint = null;
        }
    }
}
