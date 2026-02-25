// SerialLink.cs
// Raw byte transport over USB-Serial at 115200 8N1.
// No framing, no CRC, no protocol interpretation.
// All constants derived from RT5D_Protocol_Analysis.PDF §2 (Physical & Serial Layer).

using System;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace RT5D
{
    /// <summary>
    /// Raw byte transport over USB-Serial at 115200 8N1.
    /// Exposes only ReadExactAsync and WriteAsync — all framing
    /// and interpretation is handled by higher layers.
    /// </summary>
    public sealed class SerialLink : IDisposable
    {
        // ── Serial parameters (RT5D_Protocol_Analysis.PDF §2) ──────────────────
        private const int    BaudRate  = 115200;
        private const int    DataBits  = 8;
        private const Parity ParityNone = Parity.None;
        private const StopBits OneStop  = StopBits.One;

        // Default read/write timeout used when no CancellationToken deadline applies.
        // The protocol retry logic (§5.1) operates at 1 000 ms; give the raw port
        // a slightly longer window so ProtocolV2 controls the actual deadline.
        private const int DefaultPortTimeoutMs = 2000;

        private readonly SerialPort _port;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private bool _disposed;

        // ── Construction ────────────────────────────────────────────────────────

        /// <param name="portName">Platform COM port name, e.g. "COM3" or "/dev/ttyUSB0".</param>
        public SerialLink(string portName)
        {
            if (string.IsNullOrWhiteSpace(portName))
                throw new ArgumentException("Port name must not be empty.", nameof(portName));

            _port = new SerialPort(portName, BaudRate, ParityNone, DataBits, OneStop)
            {
                ReadTimeout  = DefaultPortTimeoutMs,
                WriteTimeout = DefaultPortTimeoutMs,
                // No software flow control (§2).
                // DTR and RTS must be asserted: the RT-5D programming cable uses
                // these lines to power and enable its internal switching circuitry.
                // Without them the cable sits in an undefined state and the radio
                // never receives data, even though the virtual COM port appears open.
                Handshake    = Handshake.None,
                DtrEnable    = true,
                RtsEnable    = true,
            };
        }

        // ── Lifecycle ───────────────────────────────────────────────────────────

        /// <summary>Opens the serial port.</summary>
        /// <remarks>
        /// A short delay is inserted after opening to allow the USB-serial adapter
        /// (CH340, CP210x, etc.) to assert DTR/RTS and the cable's switching
        /// circuitry to reach a stable state before the first byte is transmitted.
        /// Without this pause the handshake frame can be sent before the cable is
        /// ready and the radio never responds.
        /// </remarks>
        public void Open()
        {
            ThrowIfDisposed();
            if (!_port.IsOpen)
            {
                _port.Open();
                // Allow the adapter and cable circuitry to stabilise.
                System.Threading.Thread.Sleep(200);
            }
        }

        /// <summary>Closes the serial port without disposing.</summary>
        public void Close()
        {
            if (_port.IsOpen)
                _port.Close();
        }

        public bool IsOpen => _port.IsOpen;
        public string PortName => _port.PortName;

        // ── I/O primitives ──────────────────────────────────────────────────────
        //
        // IMPORTANT — why we use Task.Run + SerialPort.Read rather than BaseStream.ReadAsync:
        //
        // On Windows, SerialPort.BaseStream.ReadAsync does NOT honour ReadTimeout.
        // It returns 0 bytes immediately when the receive buffer is empty instead of
        // blocking until data arrives or the timeout expires. This works through a
        // virtual COM port (which buffers data before the read fires) but fails
        // against a real USB-serial adapter where the radio's response arrives
        // tens of milliseconds after the request is sent.
        //
        // SerialPort.Read() (the synchronous API) does honour ReadTimeout correctly.
        // We offload it to the thread pool via Task.Run so the calling async chain
        // is not blocked, and we link the CancellationToken so that session-level
        // timeouts still work.

        /// <summary>
        /// Writes <paramref name="buffer"/> to the serial port.
        /// Thread-safe via internal write lock.
        /// </summary>
        public async Task WriteAsync(byte[] buffer, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            if (buffer is null) throw new ArgumentNullException(nameof(buffer));
            if (buffer.Length == 0) return;

            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Write on a thread-pool thread so the async chain is not blocked.
                // SerialPort.Write() is synchronous but fast — the OS buffers the
                // bytes immediately and returns as soon as they are queued.
                await Task.Run(() => _port.Write(buffer, 0, buffer.Length), ct)
                          .ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// Reads exactly <paramref name="count"/> bytes from the serial port.
        /// Loops internally until all bytes are received or the token is cancelled.
        /// Throws <see cref="SerialLinkException"/> on timeout or port error.
        /// </summary>
        public async Task<byte[]> ReadExactAsync(int count, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (count == 0) return Array.Empty<byte>();

            var buffer   = new byte[count];
            int received = 0;

            while (received < count)
            {
                ct.ThrowIfCancellationRequested();

                int n;
                try
                {
                    // Capture locals for the lambda.
                    var port    = _port;
                    var buf     = buffer;
                    var offset  = received;
                    var want    = count - received;

                    // Run the blocking Read on the thread pool.
                    // SerialPort.Read blocks until at least 1 byte arrives or
                    // ReadTimeout elapses (throws TimeoutException on timeout).
                    n = await Task.Run(() => port.Read(buf, offset, want), ct)
                                  .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (TimeoutException)
                {
                    // Convert SerialPort timeout to OperationCanceledException so
                    // ProtocolV2's retry logic sees a standard cancellation signal.
                    throw new OperationCanceledException(
                        $"Serial read timed out after {received}/{count} bytes.", ct);
                }
                catch (Exception ex)
                {
                    throw new SerialLinkException(
                        $"Serial read failed after {received}/{count} bytes: {ex.Message}", ex);
                }

                if (n == 0)
                    throw new SerialLinkException(
                        $"Serial port returned 0 bytes (port closed?) after {received}/{count} bytes.");

                received += n;
            }

            return buffer;
        }

        /// <summary>
        /// Reads a single byte. Convenience wrapper around ReadExactAsync.
        /// </summary>
        public async Task<byte> ReadByteAsync(CancellationToken ct = default)
        {
            var b = await ReadExactAsync(1, ct).ConfigureAwait(false);
            return b[0];
        }

        /// <summary>
        /// Discards all bytes currently in the receive buffer.
        /// Used by the retry logic (§5.1) to flush stale data before retransmit.
        /// </summary>
        public void DiscardInBuffer()
        {
            ThrowIfDisposed();
            if (_port.IsOpen)
                _port.DiscardInBuffer();
        }

        /// <summary>
        /// Returns a sorted list of available serial port names on this system.
        /// On Linux, System.IO.Ports.SerialPort.GetPortNames() requires the native
        /// libSystem.IO.Ports.Native shared library. If that is absent (common in
        /// self-contained or trimmed deployments) it throws DllNotFoundException.
        /// We fall back to directly scanning /dev/ for known USB/serial prefixes,
        /// which works reliably without any native dependencies.
        /// </summary>
        public static string[] GetPortNames()
        {
            // Try the BCL method first (works when native lib is present)
            if (Environment.OSVersion.Platform != PlatformID.Unix)
            {
                // Windows — just use the BCL, it reads the registry
                try
                {
                    var names = SerialPort.GetPortNames();
                    return names ?? Array.Empty<string>();
                }
                catch
                {
                    return Array.Empty<string>();
                }
            }

            // Linux/macOS — scan /dev/ directly, no native lib needed
            var ports = new System.Collections.Generic.List<string>();
            try
            {
                // USB serial adapters (CH340, CP210x, FTDI, PL2303, …)
                ports.AddRange(Directory.GetFiles("/dev/", "ttyUSB*"));
                // CDC ACM (Arduino, many modems)
                ports.AddRange(Directory.GetFiles("/dev/", "ttyACM*"));
                // Built-in hardware UARTs
                ports.AddRange(Directory.GetFiles("/dev/", "ttyS*"));
                // Raspberry Pi / ARM SoC UARTs
                ports.AddRange(Directory.GetFiles("/dev/", "ttyAMA*"));
                // macOS USB-serial
                ports.AddRange(Directory.GetFiles("/dev/", "tty.usb*"));
                ports.AddRange(Directory.GetFiles("/dev/", "cu.usb*"));
            }
            catch { /* /dev not accessible — return what we have */ }

            ports.Sort(StringComparer.OrdinalIgnoreCase);
            return ports.ToArray();
        }

        // ── Disposal ────────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _writeLock.Dispose();
            if (_port.IsOpen) _port.Close();
            _port.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SerialLink));
        }
    }

    // ── Exception type ──────────────────────────────────────────────────────────

    /// <summary>Thrown when the serial transport encounters an unrecoverable error.</summary>
    public sealed class SerialLinkException : IOException
    {
        public SerialLinkException(string message) : base(message) { }
        public SerialLinkException(string message, Exception inner) : base(message, inner) { }
    }
}
