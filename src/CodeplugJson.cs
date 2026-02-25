// CodeplugJson.cs
// Human-readable JSON serialization and deserialization of RT-5D radio configuration.
//
// DESIGN GOALS
//   • Every field is stored as a meaningful value — no raw hex dumps.
//   • Frequencies appear as MHz strings ("146.520000"), tones as human strings ("CTCSS 88.5 Hz").
//   • Enum values use their symbolic names.
//   • Null slots are omitted from arrays; slot numbers are explicit in objects.
//   • Round-trip: JSON → SessionData → JSON must produce identical output.
//
// USAGE (replacing the old SessionDataJson / SaveJson / LoadJson):
//
//   // Save:
//   string json = CodeplugJson.Serialize(sessionData);
//   File.WriteAllText(path, json);
//
//   // Load:
//   SessionData data = CodeplugJson.Deserialize(File.ReadAllText(path));

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RT5D
{
    // ═══════════════════════════════════════════════════════════════════════════
    // Public entry-point
    // ═══════════════════════════════════════════════════════════════════════════

    public static class CodeplugJson
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented          = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        };

        /// <summary>Decodes <paramref name="data"/> and serializes to indented JSON.</summary>
        public static string Serialize(SessionData data)
        {
            var doc = CodeplugDocument.FromSessionData(data);
            return JsonSerializer.Serialize(doc, Options);
        }

        /// <summary>Parses JSON produced by <see cref="Serialize"/> back to a <see cref="SessionData"/>.</summary>
        public static SessionData Deserialize(string json)
        {
            var doc = JsonSerializer.Deserialize<CodeplugDocument>(json, Options)
                      ?? throw new InvalidOperationException("JSON deserialization returned null.");
            return doc.ToSessionData();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Top-level document
    // ═══════════════════════════════════════════════════════════════════════════

    public sealed class CodeplugDocument
    {
        [JsonPropertyName("radio")]
        public RadioInfoJson? Radio { get; set; }

        [JsonPropertyName("dtmf")]
        public DtmfJson? Dtmf { get; set; }

        [JsonPropertyName("encryptionKeys")]
        public List<EncKeyJson>? EncryptionKeys { get; set; }

        [JsonPropertyName("contacts")]
        public List<ContactJson>? Contacts { get; set; }

        [JsonPropertyName("rxGroups")]
        public List<RxGroupJson>? RxGroups { get; set; }

        [JsonPropertyName("channels")]
        public List<ChannelJson>? Channels { get; set; }

        [JsonPropertyName("vfo")]
        public VfoJson? Vfo { get; set; }

        [JsonPropertyName("settings")]
        public SettingsJson? Settings { get; set; }

        // ── From SessionData ────────────────────────────────────────────────────

        public static CodeplugDocument FromSessionData(SessionData data)
        {
            var doc = new CodeplugDocument();

            // Radio info
            var basic = BasicInfoBlock.Decode(data.BasicInfoData);
            doc.Radio = RadioInfoJson.From(basic);

            // DTMF
            var dtmf = DtmfBlock.Decode(data.DtmfData);
            doc.Dtmf = DtmfJson.From(dtmf);

            // Encryption keys
            var encBlock = EncKeyBlock.Decode(data.EncKeyData);
            doc.EncryptionKeys = new List<EncKeyJson>();
            for (int i = 0; i < EncKeyBlock.EntryCount; i++)
            {
                if (encBlock.Entries[i] is not null)
                    doc.EncryptionKeys.Add(EncKeyJson.From(i, encBlock.Entries[i]!));
            }

            // Contacts
            var contacts = ContactsPackerV2.Unpack(data.AddrBookPackets);
            doc.Contacts = new List<ContactJson>();
            for (int i = 0; i < contacts.Length; i++)
            {
                if (contacts[i] is not null)
                    doc.Contacts.Add(ContactJson.From(i + 1, contacts[i]!));
            }

            // Rx Groups
            var groups = RxGroupsPackerV2.Unpack(data.RxGroupPackets);
            doc.RxGroups = new List<RxGroupJson>();
            for (int i = 0; i < groups.Length; i++)
            {
                if (groups[i] is not null)
                    doc.RxGroups.Add(RxGroupJson.From(i + 1, groups[i]!));
            }

            // Channels
            var channels = ChannelsPackerV2.Unpack(data.ChannelPackets);
            doc.Channels = new List<ChannelJson>();
            for (int i = 0; i < channels.Length; i++)
            {
                if (channels[i] is not null)
                    doc.Channels.Add(ChannelJson.From(i + 1, channels[i]!));
            }

            // VFO
            var vfo = VfoBlock.Decode(data.VfoData);
            doc.Vfo = VfoJson.From(vfo);

            // Settings
            var optfun = OptFunBlock.Decode(data.OptFunData);
            doc.Settings = SettingsJson.From(optfun);

            return doc;
        }

        // ── To SessionData ──────────────────────────────────────────────────────

        public SessionData ToSessionData()
        {
            var data = new SessionData();

            // Basic Info
            var basic = Radio?.ToModel() ?? new BasicInfoBlock();
            data.BasicInfoData = basic.Encode();

            // DTMF
            var dtmf = Dtmf?.ToModel() ?? new DtmfBlock();
            data.DtmfData = dtmf.Encode();

            // Encryption Keys
            var encBlock = new EncKeyBlock();
            if (EncryptionKeys is not null)
            {
                foreach (var ej in EncryptionKeys)
                {
                    int slot = Math.Clamp(ej.Slot, 1, EncKeyBlock.EntryCount) - 1;
                    encBlock.Entries[slot] = ej.ToModel();
                }
            }
            data.EncKeyData = encBlock.Encode();

            // Contacts
            var contactArr = new Contact?[ContactsPackerV2.MaxContacts];
            if (Contacts is not null)
            {
                foreach (var cj in Contacts)
                {
                    int idx = Math.Clamp(cj.Slot, 1, ContactsPackerV2.MaxContacts) - 1;
                    contactArr[idx] = cj.ToModel();
                }
            }
            data.AddrBookPackets = ContactsPackerV2.Pack(contactArr);

            // Rx Groups
            var groupArr = new RxGroup?[RxGroupsPackerV2.MaxGroups];
            if (RxGroups is not null)
            {
                foreach (var gj in RxGroups)
                {
                    int idx = Math.Clamp(gj.Slot, 1, RxGroupsPackerV2.MaxGroups) - 1;
                    groupArr[idx] = gj.ToModel();
                }
            }
            data.RxGroupPackets = RxGroupsPackerV2.Pack(groupArr);

            // Channels
            var channelArr = new Channel?[ChannelsPackerV2.TotalChannels];
            if (Channels is not null)
            {
                foreach (var chj in Channels)
                {
                    int idx = Math.Clamp(chj.Slot, 1, ChannelsPackerV2.TotalChannels) - 1;
                    channelArr[idx] = chj.ToModel();
                }
            }
            data.ChannelPackets = ChannelsPackerV2.Pack(channelArr);

            // VFO
            var vfoBlock = Vfo?.ToModel() ?? new VfoBlock();
            data.VfoData = vfoBlock.Encode();

            // Settings
            var optfun = Settings?.ToModel() ?? new OptFunBlock();
            data.OptFunData = optfun.Encode();

            // Version data — no user-facing fields; leave as zeros (filled during session)
            data.VersionData = new byte[Sizes.Version];

            return data;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Radio / BasicInfo
    // ═══════════════════════════════════════════════════════════════════════════

    public sealed class RadioInfoJson
    {
        [JsonPropertyName("modelName")]
        public string ModelName { get; set; } = "DMR";

        [JsonPropertyName("modelId")]
        public int ModelId { get; set; } = 1;

        public static RadioInfoJson From(BasicInfoBlock b) =>
            new() { ModelName = b.ModelName, ModelId = b.ModelId };

        public BasicInfoBlock ToModel() =>
            new() { ModelName = ModelName, ModelId = ModelId };
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // DTMF
    // ═══════════════════════════════════════════════════════════════════════════

    public sealed class DtmfJson
    {
        [JsonPropertyName("currentId")]
        public string CurrentId { get; set; } = "";

        [JsonPropertyName("pttId")]
        public string PttId { get; set; } = "Off";

        [JsonPropertyName("duration")]
        public string Duration { get; set; } = "Ms100";

        [JsonPropertyName("interval")]
        public string Interval { get; set; } = "Ms100";

        [JsonPropertyName("codeGroups")]
        public List<string?>? CodeGroups { get; set; }

        public static DtmfJson From(DtmfBlock b)
        {
            var groups = new List<string?>();
            foreach (var g in b.CodeGroups)
                groups.Add(g);
            // Trim trailing nulls for readability
            while (groups.Count > 0 && groups[^1] is null)
                groups.RemoveAt(groups.Count - 1);

            return new DtmfJson
            {
                CurrentId  = b.CurrentId,
                PttId      = b.PttId.ToString(),
                Duration   = b.Duration.ToString(),
                Interval   = b.Interval.ToString(),
                CodeGroups = groups.Count > 0 ? groups : null,
            };
        }

        public DtmfBlock ToModel()
        {
            var b = new DtmfBlock();
            b.CurrentId = CurrentId ?? "";
            b.PttId     = Enum.TryParse<DtmfPttId>(PttId, out var pttId) ? pttId : DtmfPttId.Off;
            b.Duration  = Enum.TryParse<DtmfDuration>(Duration, out var dur) ? dur : DtmfDuration.Ms100;
            b.Interval  = Enum.TryParse<DtmfInterval>(Interval, out var inv) ? inv : DtmfInterval.Ms100;

            if (CodeGroups is not null)
            {
                for (int i = 0; i < Math.Min(CodeGroups.Count, DtmfBlock.GroupCount); i++)
                    b.CodeGroups[i] = CodeGroups[i];
            }
            return b;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Encryption Keys
    // ═══════════════════════════════════════════════════════════════════════════

    public sealed class EncKeyJson
    {
        [JsonPropertyName("slot")]
        public int Slot { get; set; } = 1;

        [JsonPropertyName("algorithm")]
        public string Algorithm { get; set; } = "Arc4";

        [JsonPropertyName("key")]
        public string Key { get; set; } = "";

        public static EncKeyJson From(int zeroIndex, EncKeyEntry e) =>
            new() { Slot = zeroIndex + 1, Algorithm = e.Algorithm.ToString(), Key = e.Key };

        public EncKeyEntry ToModel() =>
            new()
            {
                Algorithm = Enum.TryParse<EncAlgo>(Algorithm, out var a) ? a : EncAlgo.Arc4,
                Key       = Key ?? "",
            };
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Contacts
    // ═══════════════════════════════════════════════════════════════════════════

    public sealed class ContactJson
    {
        [JsonPropertyName("slot")]
        public int Slot { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("callType")]
        public string CallType { get; set; } = "Private";

        [JsonPropertyName("callId")]
        public uint CallId { get; set; }

        public static ContactJson From(int oneIndex, Contact c) =>
            new() { Slot = oneIndex, Name = c.Name, CallType = c.CallType.ToString(), CallId = c.CallId };

        public Contact ToModel() =>
            new()
            {
                Name     = Name ?? "",
                CallType = Enum.TryParse<CallType>(CallType, out var ct) ? ct : RT5D.CallType.Private,
                CallId   = CallId,
            };
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Rx Groups
    // ═══════════════════════════════════════════════════════════════════════════

    public sealed class RxGroupJson
    {
        [JsonPropertyName("slot")]
        public int Slot { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        /// <summary>List of DMR contact IDs that are members of this group.</summary>
        [JsonPropertyName("members")]
        public List<uint>? Members { get; set; }

        public static RxGroupJson From(int oneIndex, RxGroup g) =>
            new() { Slot = oneIndex, Name = g.Name, Members = g.Members.Length > 0 ? new List<uint>(g.Members) : null };

        public RxGroup ToModel() =>
            new() { Name = Name ?? "", Members = Members?.ToArray() ?? Array.Empty<uint>() };
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Channels
    // ═══════════════════════════════════════════════════════════════════════════

    public sealed class ChannelJson
    {
        [JsonPropertyName("slot")]
        public int Slot { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("type")]
        public string Type { get; set; } = "AnalogFm";

        [JsonPropertyName("rxFreqMHz")]
        public string RxFreqMHz { get; set; } = "146.000000";

        [JsonPropertyName("txFreqMHz")]
        public string TxFreqMHz { get; set; } = "146.000000";

        [JsonPropertyName("rxSubAudio")]
        public string RxSubAudio { get; set; } = "OFF";

        [JsonPropertyName("txSubAudio")]
        public string TxSubAudio { get; set; } = "OFF";

        [JsonPropertyName("power")]
        public string Power { get; set; } = "High";

        [JsonPropertyName("scanAdd")]
        public bool ScanAdd { get; set; } = true;

        [JsonPropertyName("busyLockout")]
        public bool BusyLockout { get; set; }

        [JsonPropertyName("pttId")]
        public string PttId { get; set; } = "Off";

        [JsonPropertyName("signalingCode")]
        public int SignalingCode { get; set; }

        [JsonPropertyName("scramble")]
        public int Scramble { get; set; }

        [JsonPropertyName("encMode")]
        public string EncMode { get; set; } = "None";

        [JsonPropertyName("encKeyIndex")]
        public int EncKeyIndex { get; set; }

        // DMR-specific (only meaningful when type != AnalogFm)
        [JsonPropertyName("timeSlot")]
        public int? TimeSlot { get; set; }

        [JsonPropertyName("colorCode")]
        public int? ColorCode { get; set; }

        [JsonPropertyName("rxGroupIndex")]
        public int? RxGroupIndex { get; set; }

        [JsonPropertyName("contactIndex")]
        public int? ContactIndex { get; set; }

        [JsonPropertyName("dmrRepeater")]
        public bool? DmrRepeater { get; set; }

        [JsonPropertyName("learnFhss")]
        public bool? LearnFhss { get; set; }

        [JsonPropertyName("fhssCode")]
        public string? FhssCode { get; set; }

        // ── Conversions ─────────────────────────────────────────────────────────

        public static ChannelJson From(int oneIndex, Channel c)
        {
            var j = new ChannelJson
            {
                Slot          = oneIndex,
                Name          = c.Name,
                Type          = c.Type.ToString(),
                RxFreqMHz     = $"{c.RxFreqMHz:F6}",
                TxFreqMHz     = $"{c.TxFreqMHz:F6}",
                RxSubAudio    = JsonHelpers.SubAudioToString(c.RxSubAudio),
                TxSubAudio    = JsonHelpers.SubAudioToString(c.TxSubAudio),
                Power         = c.Power.ToString(),
                ScanAdd       = c.ScanAdd,
                BusyLockout   = c.BusyLockout,
                PttId         = c.PttId.ToString(),
                SignalingCode = c.SignalingCode,
                Scramble      = c.Scramble,
                EncMode       = c.EncMode.ToString(),
                EncKeyIndex   = c.EncKeyIndex,
            };

            if (c.Type != ChannelType.AnalogFm)
            {
                j.TimeSlot     = c.TimeSlot;
                j.ColorCode    = c.ColorCode;
                j.RxGroupIndex = c.RxGroupIndex;
                j.ContactIndex = c.ContactIndex;
                j.DmrRepeater  = c.DmrRepeater;
                j.LearnFhss    = c.LearnFhss;
                j.FhssCode     = c.FhssCode;
            }

            return j;
        }

        public Channel ToModel()
        {
            var type = Enum.TryParse<ChannelType>(Type, out var t) ? t : ChannelType.AnalogFm;
            return new Channel
            {
                Name          = Name ?? "",
                Type          = type,
                RxFreqMHz     = JsonHelpers.ParseFreq(RxFreqMHz),
                TxFreqMHz     = JsonHelpers.ParseFreq(TxFreqMHz),
                RxSubAudio    = JsonHelpers.ParseSubAudio(RxSubAudio),
                TxSubAudio    = JsonHelpers.ParseSubAudio(TxSubAudio),
                Power         = Enum.TryParse<TxPower>(Power, out var pw) ? pw : TxPower.High,
                ScanAdd       = ScanAdd,
                BusyLockout   = BusyLockout,
                PttId         = Enum.TryParse<ChPttId>(PttId, out var pid) ? pid : ChPttId.Off,
                SignalingCode = SignalingCode,
                Scramble      = Scramble,
                EncMode       = Enum.TryParse<Encryption>(EncMode, out var enc) ? enc : Encryption.None,
                EncKeyIndex   = EncKeyIndex,
                TimeSlot      = TimeSlot ?? 0,
                ColorCode     = ColorCode ?? 1,
                RxGroupIndex  = RxGroupIndex ?? 0,
                ContactIndex  = (ushort)(ContactIndex ?? 0),
                DmrRepeater   = DmrRepeater ?? false,
                LearnFhss     = LearnFhss ?? false,
                FhssCode      = FhssCode,
            };
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // VFO
    // ═══════════════════════════════════════════════════════════════════════════

    public sealed class VfoJson
    {
        [JsonPropertyName("bankA")]
        public VfoBankJson? BankA { get; set; }

        [JsonPropertyName("bankB")]
        public VfoBankJson? BankB { get; set; }

        public static VfoJson From(VfoBlock v) =>
            new() { BankA = VfoBankJson.From(v.BankA), BankB = VfoBankJson.From(v.BankB) };

        public VfoBlock ToModel() =>
            new() { BankA = BankA?.ToModel() ?? new VfoBank(), BankB = BankB?.ToModel() ?? new VfoBank() };
    }

    public sealed class VfoBankJson
    {
        [JsonPropertyName("rxFreqMHz")]
        public string RxFreqMHz { get; set; } = "146.000000";

        [JsonPropertyName("txFreqMHz")]
        public string TxFreqMHz { get; set; } = "146.000000";

        [JsonPropertyName("rxSubAudio")]
        public string RxSubAudio { get; set; } = "OFF";

        [JsonPropertyName("txSubAudio")]
        public string TxSubAudio { get; set; } = "OFF";

        [JsonPropertyName("type")]
        public string Type { get; set; } = "AnalogFm";

        [JsonPropertyName("power")]
        public string Power { get; set; } = "High";

        [JsonPropertyName("step")]
        public string Step { get; set; } = "kHz25";

        [JsonPropertyName("busyLockout")]
        public bool BusyLockout { get; set; }

        [JsonPropertyName("scramble")]
        public int Scramble { get; set; }

        [JsonPropertyName("encMode")]
        public string EncMode { get; set; } = "None";

        [JsonPropertyName("encKeyIndex")]
        public int EncKeyIndex { get; set; }

        [JsonPropertyName("signalingCode")]
        public int SignalingCode { get; set; }

        [JsonPropertyName("timeSlot")]
        public int TimeSlot { get; set; }

        [JsonPropertyName("colorCode")]
        public int ColorCode { get; set; } = 1;

        [JsonPropertyName("rxGroupIndex")]
        public int RxGroupIndex { get; set; }

        [JsonPropertyName("contactIndex")]
        public int ContactIndex { get; set; }

        [JsonPropertyName("dmrRepeater")]
        public bool DmrRepeater { get; set; }

        public static VfoBankJson From(VfoBank v) =>
            new()
            {
                RxFreqMHz     = $"{v.RxFreqMHz:F6}",
                TxFreqMHz     = $"{v.TxFreqMHz:F6}",
                RxSubAudio    = JsonHelpers.SubAudioToString(v.RxSubAudio),
                TxSubAudio    = JsonHelpers.SubAudioToString(v.TxSubAudio),
                Type          = v.Type.ToString(),
                Power         = v.Power.ToString(),
                Step          = v.Step.ToString(),
                BusyLockout   = v.BusyLockout,
                Scramble      = v.Scramble,
                EncMode       = v.EncMode.ToString(),
                EncKeyIndex   = v.EncKeyIndex,
                SignalingCode = v.SignalingCode,
                TimeSlot      = v.TimeSlot,
                ColorCode     = v.ColorCode,
                RxGroupIndex  = v.RxGroupIndex,
                ContactIndex  = v.ContactIndex,
                DmrRepeater   = v.DmrRepeater,
            };

        public VfoBank ToModel() =>
            new()
            {
                RxFreqMHz     = JsonHelpers.ParseFreq(RxFreqMHz),
                TxFreqMHz     = JsonHelpers.ParseFreq(TxFreqMHz),
                RxSubAudio    = JsonHelpers.ParseSubAudio(RxSubAudio),
                TxSubAudio    = JsonHelpers.ParseSubAudio(TxSubAudio),
                Type          = Enum.TryParse<ChannelType>(Type, out var t) ? t : ChannelType.AnalogFm,
                Power         = Enum.TryParse<TxPower>(Power, out var pw) ? pw : TxPower.High,
                Step          = Enum.TryParse<StepFreq>(Step, out var st) ? st : StepFreq.kHz25,
                BusyLockout   = BusyLockout,
                Scramble      = Scramble,
                EncMode       = Enum.TryParse<Encryption>(EncMode, out var enc) ? enc : Encryption.None,
                EncKeyIndex   = EncKeyIndex,
                SignalingCode = SignalingCode,
                TimeSlot      = TimeSlot,
                ColorCode     = ColorCode,
                RxGroupIndex  = RxGroupIndex,
                ContactIndex  = (ushort)ContactIndex,
                DmrRepeater   = DmrRepeater,
            };
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Settings (OptFunBlock)
    // ═══════════════════════════════════════════════════════════════════════════

    public sealed class SettingsJson
    {
        // Part 1 — Global
        [JsonPropertyName("analogSql")]        public int    AnalogSql     { get; set; } = 3;
        [JsonPropertyName("digitalSql")]       public int    DigitalSql    { get; set; } = 3;
        [JsonPropertyName("powerSave")]        public int    PowerSave     { get; set; } = 1;
        [JsonPropertyName("vox")]              public int    Vox           { get; set; }
        [JsonPropertyName("voxDelay")]         public int    VoxDelay      { get; set; } = 5;
        [JsonPropertyName("autoBacklight")]    public int    AutoBacklight { get; set; } = 5;
        [JsonPropertyName("autoLock")]         public int    AutoLock      { get; set; }
        [JsonPropertyName("menuAutoQuit")]     public int    MenuAutoQuit  { get; set; } = 1;
        [JsonPropertyName("tdr")]              public bool   Tdr           { get; set; }
        [JsonPropertyName("tdrTxPriority")]    public int    TdrTxPriority { get; set; }
        [JsonPropertyName("tdrRecovery")]      public int    TdrRecovery   { get; set; } = 3;
        [JsonPropertyName("tot")]              public int    Tot           { get; set; }
        [JsonPropertyName("beep")]             public bool   Beep          { get; set; } = true;
        [JsonPropertyName("voice")]            public bool   Voice         { get; set; } = true;
        [JsonPropertyName("chineseLang")]      public bool   ChineseLang   { get; set; }
        [JsonPropertyName("sideTone")]         public string SideTone      { get; set; } = "Off";
        [JsonPropertyName("scanMode")]         public string ScanMode      { get; set; } = "CarrierOperated";
        [JsonPropertyName("globalPttId")]      public string GlobalPttId   { get; set; } = "Off";
        [JsonPropertyName("idDelayTime")]      public int    IdDelayTime   { get; set; } = 4;
        [JsonPropertyName("displayA")]         public string DisplayA      { get; set; } = "Name";
        [JsonPropertyName("displayB")]         public string DisplayB      { get; set; } = "Name";
        [JsonPropertyName("sosMode")]          public string SosMode       { get; set; } = "OnSite";
        [JsonPropertyName("alarmSound")]       public bool   AlarmSound    { get; set; } = true;
        [JsonPropertyName("tailClear")]        public bool   TailClear     { get; set; } = true;
        [JsonPropertyName("rptClearTail")]     public int    RptClearTail  { get; set; }
        [JsonPropertyName("rptDetectTail")]    public int    RptDetectTail { get; set; }
        [JsonPropertyName("txOverSound")]      public bool   TxOverSound   { get; set; }
        [JsonPropertyName("workBandIsB")]      public bool   WorkBandIsB   { get; set; }
        [JsonPropertyName("fmRadioEnabled")]   public bool   FmRadioEnabled{ get; set; } = true;
        [JsonPropertyName("workModeACh")]      public bool   WorkModeACh   { get; set; }
        [JsonPropertyName("workModeBCh")]      public bool   WorkModeBCh   { get; set; }
        [JsonPropertyName("keyLock")]          public bool   KeyLock       { get; set; }
        [JsonPropertyName("bootVoltage")]      public bool   BootVoltage   { get; set; }
        [JsonPropertyName("rTone")]            public string RTone         { get; set; } = "Hz1750";
        [JsonPropertyName("txStartSound")]     public bool   TxStartSound  { get; set; }
        [JsonPropertyName("steFreq")]          public string SteFreq       { get; set; } = "Hz55";
        [JsonPropertyName("weatherCh")]        public int    WeatherCh     { get; set; }
        [JsonPropertyName("keepCallTime")]     public int    KeepCallTime  { get; set; } = 4;

        // Button assignments
        [JsonPropertyName("topKey1S")]   public string TopKey1S  { get; set; } = "Radio";
        [JsonPropertyName("sideKey2S")]  public string SideKey2S { get; set; } = "Moni";
        [JsonPropertyName("sideKey2L")]  public string SideKey2L { get; set; } = "Scan";
        [JsonPropertyName("sideKey3S")]  public string SideKey3S { get; set; } = "Search";
        [JsonPropertyName("sideKey3L")]  public string SideKey3L { get; set; } = "Sos";

        public static SettingsJson From(OptFunBlock o) =>
            new()
            {
                AnalogSql     = o.AnalogSql,
                DigitalSql    = o.DigitalSql,
                PowerSave     = o.PowerSave,
                Vox           = o.Vox,
                VoxDelay      = o.VoxDelay,
                AutoBacklight = o.AutoBacklight,
                AutoLock      = o.AutoLock,
                MenuAutoQuit  = o.MenuAutoQuit,
                Tdr           = o.Tdr,
                TdrTxPriority = o.TdrTxPriority,
                TdrRecovery   = o.TdrRecovery,
                Tot           = o.Tot,
                Beep          = o.Beep,
                Voice         = o.Voice,
                ChineseLang   = o.ChineseLang,
                SideTone      = o.SideTone.ToString(),
                ScanMode      = o.ScanMode.ToString(),
                GlobalPttId   = o.GlobalPttId.ToString(),
                IdDelayTime   = o.IdDelayTime,
                DisplayA      = o.DisplayA.ToString(),
                DisplayB      = o.DisplayB.ToString(),
                SosMode       = o.SosMode.ToString(),
                AlarmSound    = o.AlarmSound,
                TailClear     = o.TailClear,
                RptClearTail  = o.RptClearTail,
                RptDetectTail = o.RptDetectTail,
                TxOverSound   = o.TxOverSound,
                WorkBandIsB   = o.WorkBandIsB,
                FmRadioEnabled= !o.FmRadioOff,   // inverted: FmRadioOff=1 means FM disabled
                WorkModeACh   = o.WorkModeACh,
                WorkModeBCh   = o.WorkModeBCh,
                KeyLock       = o.KeyLock,
                BootVoltage   = o.BootVoltage,
                RTone         = o.RTone.ToString(),
                TxStartSound  = o.TxStartSound,
                SteFreq       = o.SteFreq.ToString(),
                WeatherCh     = o.WeatherCh,
                KeepCallTime  = o.KeepCallTime,
                TopKey1S      = o.TopKey1S.ToString(),
                SideKey2S     = o.SideKey2S.ToString(),
                SideKey2L     = o.SideKey2L.ToString(),
                SideKey3S     = o.SideKey3S.ToString(),
                SideKey3L     = o.SideKey3L.ToString(),
            };

        public OptFunBlock ToModel() =>
            new()
            {
                AnalogSql     = AnalogSql,
                DigitalSql    = DigitalSql,
                PowerSave     = PowerSave,
                Vox           = Vox,
                VoxDelay      = VoxDelay,
                AutoBacklight = AutoBacklight,
                AutoLock      = AutoLock,
                MenuAutoQuit  = MenuAutoQuit,
                Tdr           = Tdr,
                TdrTxPriority = TdrTxPriority,
                TdrRecovery   = TdrRecovery,
                Tot           = Tot,
                Beep          = Beep,
                Voice         = Voice,
                ChineseLang   = ChineseLang,
                SideTone      = Enum.TryParse<SideTone>(SideTone, out var st) ? st : RT5D.SideTone.Off,
                ScanMode      = Enum.TryParse<ScanMode>(ScanMode, out var sm) ? sm : RT5D.ScanMode.CarrierOperated,
                GlobalPttId   = Enum.TryParse<ChPttId>(GlobalPttId, out var gp) ? gp : ChPttId.Off,
                IdDelayTime   = IdDelayTime,
                DisplayA      = Enum.TryParse<DisplayMode>(DisplayA, out var da) ? da : RT5D.DisplayMode.Name,
                DisplayB      = Enum.TryParse<DisplayMode>(DisplayB, out var db) ? db : RT5D.DisplayMode.Name,
                SosMode       = Enum.TryParse<SosMode>(SosMode, out var sos) ? sos : RT5D.SosMode.OnSite,
                AlarmSound    = AlarmSound,
                TailClear     = TailClear,
                RptClearTail  = RptClearTail,
                RptDetectTail = RptDetectTail,
                TxOverSound   = TxOverSound,
                WorkBandIsB   = WorkBandIsB,
                FmRadioOff    = !FmRadioEnabled,
                WorkModeACh   = WorkModeACh,
                WorkModeBCh   = WorkModeBCh,
                KeyLock       = KeyLock,
                BootVoltage   = BootVoltage,
                RTone         = Enum.TryParse<RTone>(RTone, out var rt) ? rt : RT5D.RTone.Hz1750,
                TxStartSound  = TxStartSound,
                SteFreq       = Enum.TryParse<SteFreq>(SteFreq, out var sf) ? sf : RT5D.SteFreq.Hz55,
                WeatherCh     = WeatherCh,
                KeepCallTime  = KeepCallTime,
                TopKey1S      = Enum.TryParse<KeyFunc>(TopKey1S,  out var k1) ? k1 : KeyFunc.Radio,
                SideKey2S     = Enum.TryParse<KeyFunc>(SideKey2S, out var k2) ? k2 : KeyFunc.Moni,
                SideKey2L     = Enum.TryParse<KeyFunc>(SideKey2L, out var k3) ? k3 : KeyFunc.Scan,
                SideKey3S     = Enum.TryParse<KeyFunc>(SideKey3S, out var k4) ? k4 : KeyFunc.Search,
                SideKey3L     = Enum.TryParse<KeyFunc>(SideKey3L, out var k5) ? k5 : KeyFunc.Sos,
            };
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Sub-audio helpers (shared by Channel and VFO serializers)
    // ═══════════════════════════════════════════════════════════════════════════

    internal static class JsonHelpers
    {
        public static string SubAudioToString(SubAudio s) => s.Type switch
        {
            SubAudioType.Off   => "OFF",
            SubAudioType.Ctcss => $"CTCSS {s.CtcssHz:F1}",
            SubAudioType.Dcs   => s.DcsCode,
            _                  => "OFF",
        };

        public static SubAudio ParseSubAudio(string? s)
        {
            if (string.IsNullOrWhiteSpace(s) || s.Equals("OFF", StringComparison.OrdinalIgnoreCase))
                return SubAudio.Off();

            if (s.StartsWith("CTCSS ", StringComparison.OrdinalIgnoreCase))
            {
                if (float.TryParse(s[6..].Trim(), System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out float hz))
                    return SubAudio.Ctcss(hz);
                return SubAudio.Off();
            }

            // DCS — exact code string like "D023N"
            if (Array.IndexOf(SubAudio.DcsTable, s.ToUpperInvariant()) >= 0)
                return SubAudio.Dcs(s.ToUpperInvariant());

            return SubAudio.Off();
        }

        public static double ParseFreq(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double mhz))
                return mhz;
            return 0;
        }
    }
}
