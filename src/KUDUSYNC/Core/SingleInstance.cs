// Keeps one main window and one tray part per user (and data folder): later starts hand their command line over through
// a pipe.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Kudusync.Core
{
    /// <summary>
    /// Keeps one KUDUSYNC window per user: a second start (from the right-click menu, the tray or a notification)
    /// hands its command line to the window that's already open, through a named pipe, and exits.
    /// </summary>
    public sealed class SingleInstance : IDisposable
    {
        /// <summary>The name of the lock and the pipe.</summary>
        private readonly string _name;
        /// <summary>The lock, held by the first copy.</summary>
        private readonly Mutex _mutex;
        /// <summary>Stops listening.</summary>
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();

        /// <summary>True when this is the first copy.</summary>
        public bool IsFirst { get; }

        /// <summary>Takes the lock for this part ("main" or "tray").</summary>
        public SingleInstance(string role)
        {
            _name = "KUDUSYNC-" + role + "-" + DataStore.StableHash(Environment.UserName + "|" + DataStore.Folder);
            _mutex = new Mutex(true, _name, out var created);
            IsFirst = created;
        }

        /// <summary>Listens for command lines from later starts; <paramref name="received"/> runs on a background thread.</summary>
        public void Listen(Action<string[]> received)
        {
            Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    try
                    {
                        using (var server = new NamedPipeServerStream(_name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                        {
                            await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                            using (var reader = new StreamReader(server, Encoding.UTF8))
                            {
                                var args = new List<string>();
                                string line;
                                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null) args.Add(line);
                                received(args.ToArray());
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (IOException)
                    {
                        await Task.Delay(200).ConfigureAwait(false);
                    }
                }
            });
        }

        /// <summary>Sends this start's command line to the first one. Returns false if it couldn't be reached.</summary>
        public bool Forward(string[] args)
        {
            try
            {
                using (var client = new NamedPipeClientStream(".", _name, PipeDirection.Out))
                {
                    client.Connect(3000);
                    using (var writer = new StreamWriter(client, new UTF8Encoding(false)))
                        foreach (var a in args) writer.WriteLine(a.Replace("\r", " ").Replace("\n", " "));
                }
                return true;
            }
            catch (Exception ex) when (ex is TimeoutException || ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>Stops listening and lets go of the lock.</summary>
        public void Dispose()
        {
            _stop.Cancel();
            if (IsFirst)
            {
                try { _mutex.ReleaseMutex(); }
                catch (ApplicationException) { }
            }
            _mutex.Dispose();
        }
    }
}
