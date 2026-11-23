using System;
using System.Collections.Generic;
using System.Threading;
using MksFanHub;

// Порт-эмулятор: файловый поток на pty вместо SerialPort.
// Один постоянный читатель на путь (закрытие fd при блокирующем read на pty не прерывает чтение).
class FilePortLink : ISerialLink
{
    private class Shared { public System.IO.FileStream Fs; public Queue<byte> Rx = new Queue<byte>(); }
    private static readonly Dictionary<string, Shared> shared = new Dictionary<string, Shared>();
    private readonly Shared sh;

    public FilePortLink(string path)
    {
        lock (shared)
        {
            if (!shared.TryGetValue(path, out sh))
            {
                sh = new Shared();
                sh.Fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.ReadWrite, System.IO.FileShare.ReadWrite, 1, false);
                shared[path] = sh;
                Shared local = sh;
                Thread t = new Thread(delegate () {
                    byte[] b = new byte[256];
                    while (true)
                    {
                        int n = 0;
                        try { n = local.Fs.Read(b, 0, b.Length); } catch { break; }
                        if (n <= 0) { Thread.Sleep(10); continue; }
                        lock (local.Rx) { for (int i = 0; i < n; i++) local.Rx.Enqueue(b[i]); }
                    }
                });
                t.IsBackground = true;
                t.Start();
            }
        }
        lock (sh.Rx) { sh.Rx.Clear(); }
    }
    public void Write(string text) { byte[] d = System.Text.Encoding.ASCII.GetBytes(text); sh.Fs.Write(d, 0, d.Length); sh.Fs.Flush(); }
    public int BytesToRead { get { lock (sh.Rx) { return sh.Rx.Count; } } }
    public int Read(byte[] buffer, int offset, int count)
    {
        lock (sh.Rx) { int n = 0; while (n < count && sh.Rx.Count > 0) buffer[offset + n++] = sh.Rx.Dequeue(); return n; }
    }
    public void DiscardInBuffer() { lock (sh.Rx) { sh.Rx.Clear(); } }
    public string ReadLine()
    {
        DateTime until = DateTime.Now.AddMilliseconds(500);
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        while (DateTime.Now < until)
        {
            lock (sh.Rx) { while (sh.Rx.Count > 0) { char c = (char)sh.Rx.Dequeue(); if (c == '\n') return sb.ToString(); sb.Append(c); } }
            Thread.Sleep(5);
        }
        throw new TimeoutException();
    }
    public void Close() { /* fd остаётся открытым общим читателем */ }
}
