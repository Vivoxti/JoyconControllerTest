using System;

namespace Joycon2
{
    /// <summary>The operations a session needs from one connected device (implemented on top of a transport).</summary>
    public interface IJoycon2Link
    {
        void Subscribe(string characteristicUuid, int tag);
        void Write(string characteristicUuid, byte[] data);
    }

    public sealed class Joycon2SessionOptions
    {
        public byte FeatureMask = Joycon2Protocol.DefaultFeatureMask;
        public double ResponseTimeout = 1.0;
        public int CommandAttempts = 2;
        public double SubscribeTimeout = 5.0;
        /// <summary>Gap between commands when the response characteristic is unavailable.</summary>
        public double BlindCommandDelay = 0.3;
        /// <summary>macOS drops the link ~10–17 s after the host's last write; harmless everywhere else.</summary>
        public double KeepAliveInterval = 1.0;
    }

    /// <summary>
    /// Handshake and steady state of one Joy-Con 2, transport-agnostic and engine-agnostic.
    /// Drive it with <see cref="OnConnected"/>, <see cref="OnSubscribed"/>, <see cref="OnNotification"/>
    /// and <see cref="Update"/>; it calls back into <see cref="IJoycon2Link"/>.
    ///
    /// Handshake (PROTOCOL.md): subscribe responses → (read info: L/R, only when the side is unknown) →
    /// read user calibration → (factory calibration) → player LED → feature mask set → feature mask enable → subscribe input.
    /// Failed command steps are skipped; only a failed input subscription fails the session.
    /// </summary>
    public sealed class Joycon2Session
    {
        public const int TagCommandResponse = 1;
        public const int TagInput = 2;

        public enum SessionState { Connecting, Handshake, Ready, Failed }
        public enum CalibrationSource { Default, Factory, User }

        enum Step { None, SubscribeResponses, ReadInfo, ReadUserCalibration, ReadFactoryCalibration, SetLed, SetFeatureMask, EnableFeatures, SubscribeInput, Done }

        const int UserCalibrationReadLength = 0x30;
        const int FactoryCalibrationReadLength = Joycon2Protocol.FactoryStick2Offset + Joycon2StickCalibration.Length;

        readonly IJoycon2Link link;
        readonly Joycon2SessionOptions options;

        Step step;
        double deadline;
        int attempts;
        byte[] pendingCommand;
        bool responsesAvailable = true;
        double lastWriteTime;
        byte playerLed;

        /// <summary>L or R. When the session starts without it (Web Bluetooth gives no advertisement data and maybe
        /// no name) it is read from the controller's info block; Left until then.</summary>
        public Joycon2Side Side { get; private set; }
        public bool SideKnown { get; private set; }
        public SessionState State { get; private set; } = SessionState.Connecting;
        public string Status { get; private set; } = "connecting";
        public Joycon2StickCalibration Calibration { get; private set; } = Joycon2StickCalibration.Default;
        public CalibrationSource CalibratedFrom { get; private set; } = CalibrationSource.Default;

        public bool HasReport { get; private set; }
        public Joycon2Report LastReport { get; private set; }
        public long ReportCount { get; private set; }
        public double LastReportTime { get; private set; }

        public Action<string> Log;

        public Joycon2Session(IJoycon2Link link, Joycon2Side? side, Joycon2SessionOptions options = null)
        {
            this.link = link ?? throw new ArgumentNullException(nameof(link));
            this.options = options ?? new Joycon2SessionOptions();
            Side = side ?? Joycon2Side.Left;
            SideKnown = side != null;
        }

        /// <summary>LED pattern (4-bit mask). Sent during the handshake and re-sent as keep-alive.</summary>
        public byte PlayerLed
        {
            get => playerLed;
            set
            {
                if (playerLed == value) return;
                playerLed = value;
                if (State == SessionState.Ready) Write(Joycon2Protocol.SetPlayerLed(playerLed), lastWriteTime);
            }
        }

        public void OnConnected(double now)
        {
            if (State != SessionState.Connecting) return;
            State = SessionState.Handshake;
            Begin(Step.SubscribeResponses, now);
        }

        public void OnSubscribed(int tag, int status, double now)
        {
            if (step == Step.SubscribeResponses && tag == TagCommandResponse)
            {
                if (status != 0)
                {
                    responsesAvailable = false;
                    Warn($"command responses unavailable (status {status}); continuing without them");
                }
                Begin(Step.ReadInfo, now);
            }
            else if (step == Step.SubscribeInput && tag == TagInput)
            {
                if (status != 0) Fail($"input subscription failed (status {status})");
                else Finish(now);
            }
        }

        /// <summary>Returns true when the notification was a new input report.</summary>
        public bool OnNotification(int tag, byte[] data, int length, double now)
        {
            if (tag == TagInput)
            {
                if (!Joycon2Report.TryParse(data, length, out var report)) return false;
                LastReport = report;
                HasReport = true;
                ReportCount++;
                LastReportTime = now;
                return true;
            }

            if (tag == TagCommandResponse && pendingCommand != null)
                HandleResponse(data, length, now);
            return false;
        }

        public void Update(double now)
        {
            if (State == SessionState.Ready)
            {
                if (now - lastWriteTime >= options.KeepAliveInterval)
                    Write(Joycon2Protocol.SetPlayerLed(playerLed), now);
                return;
            }
            if (State != SessionState.Handshake || now < deadline) return;

            switch (step)
            {
                case Step.SubscribeResponses:
                    responsesAvailable = false;
                    Warn("no answer to the command-response subscription; continuing without responses");
                    Begin(Step.ReadInfo, now);
                    break;
                case Step.SubscribeInput:
                    Fail("input subscription timed out");
                    break;
                default:
                    if (responsesAvailable && attempts < options.CommandAttempts)
                    {
                        attempts++;
                        SendPending(now);
                    }
                    else
                    {
                        if (responsesAvailable) Warn($"{step}: no response, skipping");
                        Advance(now);
                    }
                    break;
            }
        }

        // ───────────────────────────── Steps ─────────────────────────────

        void Begin(Step next, double now)
        {
            step = next;
            attempts = 1;
            pendingCommand = null;
            Status = next.ToString();

            switch (next)
            {
                case Step.SubscribeResponses:
                    link.Subscribe(Joycon2Protocol.CommandResponseUuid, TagCommandResponse);
                    deadline = now + options.SubscribeTimeout;
                    break;
                case Step.ReadInfo:
                    if (SideKnown || !responsesAvailable) { Advance(now); return; }
                    SendCommand(Joycon2Protocol.ReadMemory(Joycon2Protocol.InfoAddress, Joycon2Protocol.InfoReadLength), now);
                    break;
                case Step.ReadUserCalibration:
                    if (!responsesAvailable) { Advance(now); return; }
                    SendCommand(Joycon2Protocol.ReadMemory(Joycon2Protocol.UserStickCalibrationAddress, UserCalibrationReadLength), now);
                    break;
                case Step.ReadFactoryCalibration:
                    if (!responsesAvailable || CalibratedFrom == CalibrationSource.User) { Advance(now); return; }
                    SendCommand(Joycon2Protocol.ReadMemory(Joycon2Protocol.FactoryStickCalibrationAddress, FactoryCalibrationReadLength), now);
                    break;
                case Step.SetLed:
                    SendCommand(Joycon2Protocol.SetPlayerLed(playerLed), now);
                    break;
                case Step.SetFeatureMask:
                    SendCommand(Joycon2Protocol.SetFeatureMask(options.FeatureMask), now);
                    break;
                case Step.EnableFeatures:
                    SendCommand(Joycon2Protocol.EnableFeatures(options.FeatureMask), now);
                    break;
                case Step.SubscribeInput:
                    link.Subscribe(Joycon2Protocol.InputReportUuid, TagInput);
                    deadline = now + options.SubscribeTimeout;
                    break;
            }
        }

        void Advance(double now)
        {
            if (step < Step.SubscribeInput) Begin(step + 1, now);
        }

        void SendCommand(byte[] command, double now)
        {
            pendingCommand = command;
            SendPending(now);
        }

        void SendPending(double now)
        {
            Write(pendingCommand, now);
            deadline = now + (responsesAvailable ? options.ResponseTimeout : options.BlindCommandDelay);
        }

        void HandleResponse(byte[] data, int length, double now)
        {
            byte cmd = pendingCommand[0], sub = pendingCommand[3];
            if (!Joycon2Protocol.IsResponseTo(data, length, cmd, sub)) return;

            if (cmd == Joycon2Protocol.CmdMemory)
            {
                // Echo of the request: [8] length, [12..15] address. Ignore a late answer to another read.
                if (length < Joycon2Protocol.MemoryReadDataOffset) return;
                for (int i = 0; i < 4; i++)
                    if (data[12 + i] != pendingCommand[12 + i]) return;

                if (step == Step.ReadInfo)
                    TryReadSide(data, length);
                else if (step == Step.ReadUserCalibration)
                    TryUseCalibration(data, length, Joycon2Protocol.UserStick1Offset, Joycon2Protocol.UserStick2Offset, CalibrationSource.User);
                else if (step == Step.ReadFactoryCalibration)
                    TryUseCalibration(data, length, 0, Joycon2Protocol.FactoryStick2Offset, CalibrationSource.Factory);
            }
            Advance(now);
        }

        void TryReadSide(byte[] data, int length)
        {
            int at = Joycon2Protocol.MemoryReadDataOffset + Joycon2Protocol.InfoProductIdOffset;
            if (length < at + 2) return;
            ushort pid = (ushort)(data[at] | data[at + 1] << 8);
            if (pid == Joycon2Protocol.ProductIdLeft) Side = Joycon2Side.Left;
            else if (pid == Joycon2Protocol.ProductIdRight) Side = Joycon2Side.Right;
            else
            {
                Warn($"info block: unexpected product id 0x{pid:X4}");
                return;
            }
            SideKnown = true;
            Log?.Invoke($"info block: product id 0x{pid:X4} → {Side}");
        }

        void TryUseCalibration(byte[] data, int length, int stick1, int stick2, CalibrationSource source)
        {
            int start = Joycon2Protocol.MemoryReadDataOffset;
            // Both sides keep their calibration in slot 1 (the right Joy-Con reports in field 2 but calibrates
            // with slot 1); slot 2 is only a fallback for the right one.
            if (Joycon2StickCalibration.TryParse(data, start + stick1, length, out var cal) ||
                Side == Joycon2Side.Right && Joycon2StickCalibration.TryParse(data, start + stick2, length, out cal))
            {
                Calibration = cal;
                CalibratedFrom = source;
                Log?.Invoke($"{Side}: {source} stick calibration {cal}");
            }
        }

        void Finish(double now)
        {
            step = Step.Done;
            pendingCommand = null;
            State = SessionState.Ready;
            Status = "ready";
            lastWriteTime = now;
            if (CalibratedFrom == CalibrationSource.Default) Warn("no stick calibration read; using defaults");
            if (!SideKnown) Warn("could not read L/R from the controller; assuming Left");
        }

        void Write(byte[] data, double now)
        {
            link.Write(Joycon2Protocol.CommandUuid, data);
            lastWriteTime = now;
        }

        void Fail(string reason)
        {
            State = SessionState.Failed;
            Status = reason;
            step = Step.None;
            pendingCommand = null;
            Warn(reason);
        }

        void Warn(string message) => Log?.Invoke($"{Side}: {message}");
    }
}
