﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using xunit.runner.data;
using xunit.runner.wpf.ViewModel;

namespace xunit.runner.wpf.Impl
{
    internal sealed partial class RemoteTestUtil : ITestUtil
    {
        private sealed class Connection : IDisposable
        {
            private NamedPipeClientStream _stream;
            private Process _process;

            internal NamedPipeClientStream Stream => _stream;

            internal Connection(NamedPipeClientStream stream, Process process)
            {
                _stream = stream;
                _process = process;
            }

            void IDisposable.Dispose()
            {
                if (_process != null)
                {
                    Debug.Assert(_stream != null);

                    try
                    {
                        _stream.WriteByte(0);
                    }
                    catch
                    {
                        // Signal to server we are done with the connection.  Okay to fail because
                        // it means the server isn't listening anymore.
                    }

                    _stream.Close();

                    try
                    {
                        _process.Kill();
                    }
                    catch 
                    {
                        // Inherent race condition shutting down the process.
                    }
                }
            }
        }

        private static Connection StartWorkerProcess(string action, string argument)
        {
            var pipeName = $"xunit.runner.wpf.pipe.{Guid.NewGuid()}";
            var processStartInfo = new ProcessStartInfo();
            processStartInfo.FileName = typeof(xunit.runner.worker.Program).Assembly.Location;
            processStartInfo.Arguments = $"{pipeName} {action} {argument}";
            processStartInfo.WindowStyle = ProcessWindowStyle.Hidden;

            var process = Process.Start(processStartInfo);
            try
            {
                var stream = new NamedPipeClientStream(pipeName);
                stream.Connect();
                return new Connection(stream, process);
            }
            catch
            {
                process.Kill();
                throw;
            }
        }

        private List<TestCaseViewModel> Discover(string assemblyPath)
        {
            var list = DiscoverCore(assemblyPath);
            return list
                .Select(x => new TestCaseViewModel(x.SerializedForm, x.DisplayName, x.AssemblyPath))
                .ToList();
        }

        private List<TestCaseData> DiscoverCore(string assemblyPath)
        {
            var list = new List<TestCaseData>();

            using (var connection = StartWorkerProcess(Constants.ActionDiscover, assemblyPath))
            using (var reader = new BinaryReader(connection.Stream, Constants.Encoding, leaveOpen: true))
            {
                try
                {
                    while (connection.Stream.IsConnected)
                    {
                        var testCaseData = TestCaseData.ReadFrom(reader);
                        list.Add(testCaseData);
                    }
                }
                catch
                {
                    // Hacky way of catching end of stream
                }
            }

            return list;
        }

        private RunSession Run(Dispatcher dispatcher, string assemblyPath, CancellationToken cancellationToken)
        {
            var connection = StartWorkerProcess(Constants.ActionRun, assemblyPath);
            var queue = new ConcurrentQueue<TestResultData>();
            var backgroundRunner = new BackgroundRunner(queue, new BinaryReader(connection.Stream, Constants.Encoding, leaveOpen: true), cancellationToken);
            Task.Run(backgroundRunner.GoOnBackground);

            return new RunSession(connection, dispatcher, queue);
        }

        #region ITestUtil

        List<TestCaseViewModel> ITestUtil.Discover(string assemblyPath)
        {
            return Discover(assemblyPath);
        }

        ITestRunSession ITestUtil.Run(Dispatcher dispatcher, string assemblyPath, CancellationToken cancellationToken)
        {
            return Run(dispatcher, assemblyPath, cancellationToken);
        }

        #endregion
    }
}
