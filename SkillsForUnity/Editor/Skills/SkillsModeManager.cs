using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace UnitySkills
{
    /// <summary>
    /// Outcome of <see cref="SkillsModeManager.TryGrantDetailed"/>.
    /// Lets HTTP handlers distinguish "pending panel approval" (a normal Panel-channel state)
    /// from "invalid/expired token" (an error).
    /// </summary>
    public enum GrantOutcome
    {
        Granted,
        PendingApproval,
        Invalid,
    }

    /// <summary>
    /// Public, UI-visible view of an outstanding grant request.
    /// Returned by <see cref="SkillsModeManager.PendingGrantRequests"/>; the UI panel renders
    /// these as cards with Approve/Deny buttons.
    /// </summary>
    public sealed class GrantRequest
    {
        public string Token;
        public string SkillName;
        public string ArgsSummary;
        public DateTime ExpiresAtUtc;
        /// <summary>True after the user clicks Approve on the panel (Panel channel only).</summary>
        public bool ApprovedByPanel;
        /// <summary>"dialog" or "panel" — wire string for REST responses.</summary>
        public string Channel;
    }

    /// <summary>
    /// Core of the v1.9 Skill mode permission system. Three-tier operating modes
    /// (Approval / Auto / Bypass) + two-channel approval (Dialog / Panel) + per-skill
    /// permanent grant list.
    ///
    /// State storage:
    /// - <c>CurrentMode</c> / <c>PanelApprovalRequired</c> / <c>GrantedSkills</c>: EditorPrefs (per-machine)
    /// - Pending grant tokens: in-memory only (TTL 5 min, max 256 live)
    ///
    /// Upgrade compatibility: an install that already has any pre-v1.9 <c>UnitySkills_*</c>
    /// pref (e.g. <c>UnitySkills_PreferredPort</c>) defaults to <see cref="SkillsOperatingMode.Bypass"/>
    /// so existing users see zero behavior change; fresh installs default to
    /// <see cref="SkillsOperatingMode.Auto"/> (SemiAuto whitelist + FullAuto blocked by default,
    /// no popup unless an AI tries something restricted — keeps onboarding friction low).
    /// </summary>
    public static class SkillsModeManager
    {
        public enum AccessResult { Allowed, NeedsGrant, Forbidden }
        public enum ApprovalChannel { Dialog, Panel }

        private const string PrefKeyMode = "UnitySkills_OperatingMode";
        private const string PrefKeyPanelApproval = "UnitySkills_PanelApprovalRequired";
        private const string PrefKeyGranted = "UnitySkills_GrantedSkills";

        private const int DefaultGrantTtlSeconds = 300;
        private const int MaxLiveGrants = 256;
        private const int MaxArgsSummaryChars = 120;

        // Skills that cannot be statically classified as forbidden via metadata alone.
        // The 5-10 entry budget is intentional — the goal is "self-evident high-risk operations
        // the metadata can't see"; everything else should be caught by IsForbiddenInSemi.
        private static readonly HashSet<string> _explicitNeverList = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "scene_clear",
            "scene_new",
            "batch_apply",
        };

        private sealed class GrantEntry
        {
            public string Token;
            public string SkillName;
            public string ArgsHash;
            public string ArgsSummary;
            public DateTime IssuedAtUtc;
            public DateTime ExpiresAtUtc;
            public ApprovalChannel Channel;
            public bool ApprovedByPanel;
        }

        private static readonly ConcurrentDictionary<string, GrantEntry> _grants =
            new ConcurrentDictionary<string, GrantEntry>(StringComparer.Ordinal);

        private static readonly object _grantedLock = new object();
        private static HashSet<string> _granted;

        public static event Action OnChanged;

        // ===== Properties =====

        /// <summary>
        /// Current operating mode. Setter persists to EditorPrefs and raises <see cref="OnChanged"/>.
        /// Getter applies upgrade-compat rule: if no explicit pref but other UnitySkills_* keys
        /// exist, returns <see cref="SkillsOperatingMode.Bypass"/> (existing install).
        /// </summary>
        public static SkillsOperatingMode CurrentMode
        {
            get
            {
                if (EditorPrefs.HasKey(PrefKeyMode))
                {
                    var raw = EditorPrefs.GetString(PrefKeyMode, string.Empty);
                    if (Enum.TryParse<SkillsOperatingMode>(raw, true, out var parsed))
                        return parsed;
                }
                return IsExistingInstall() ? SkillsOperatingMode.Bypass : SkillsOperatingMode.Auto;
            }
            set
            {
                EditorPrefs.SetString(PrefKeyMode, value.ToString());
                SkillsAuditLog.Append("mode_changed", new { mode = value.ToString().ToLowerInvariant() });
                RaiseChanged();
            }
        }

        /// <summary>
        /// When true (Approval mode only), AI-issued grant requests must be approved on
        /// the Unity panel before <see cref="TryGrant"/> succeeds. Default false → Dialog
        /// channel (AI obtains user consent over chat and calls grant directly).
        /// </summary>
        public static bool PanelApprovalRequired
        {
            get => EditorPrefs.GetBool(PrefKeyPanelApproval, false);
            set
            {
                EditorPrefs.SetBool(PrefKeyPanelApproval, value);
                RaiseChanged();
            }
        }

        public static IReadOnlyCollection<string> GrantedSkills
        {
            get
            {
                EnsureGrantedLoaded();
                lock (_grantedLock)
                {
                    return _granted.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray();
                }
            }
        }

        public static IReadOnlyList<GrantRequest> PendingGrantRequests
        {
            get
            {
                CleanupExpired();
                return _grants.Values
                    .OrderBy(e => e.IssuedAtUtc)
                    .Select(ToPublic)
                    .ToList();
            }
        }

        // ===== Public API =====

        /// <summary>
        /// Issue a fresh grant request token bound to (skillName, argsHash, channel, TTL).
        /// AI re-plays the token via <see cref="TryGrant"/>. For Panel channel the token is
        /// also visible in <see cref="PendingGrantRequests"/> for panel-side Approve/Deny.
        /// </summary>
        public static (string token, int ttlSeconds, ApprovalChannel channel)
            IssueGrantRequest(string skillName, string argsJson)
        {
            CleanupExpired();
            EnforceCapacity();

            var channel = PanelApprovalRequired ? ApprovalChannel.Panel : ApprovalChannel.Dialog;
            var nowUtc = DateTime.UtcNow;
            var entry = new GrantEntry
            {
                Token = GenerateToken(),
                SkillName = skillName ?? string.Empty,
                ArgsHash = HashArgs(argsJson),
                ArgsSummary = SummarizeArgs(argsJson),
                IssuedAtUtc = nowUtc,
                ExpiresAtUtc = nowUtc.AddSeconds(DefaultGrantTtlSeconds),
                Channel = channel,
                ApprovedByPanel = false,
            };
            _grants[entry.Token] = entry;

            SkillsAuditLog.Append("mode_restricted_hit", new
            {
                skill = entry.SkillName,
                grantToken = entry.Token,
                channel = ChannelToWire(channel),
                argsSummary = entry.ArgsSummary,
            });
            RaiseChanged();
            return (entry.Token, DefaultGrantTtlSeconds, channel);
        }

        /// <summary>
        /// Consume a grant token. Returns true only on full Granted outcome.
        /// HTTP handlers wanting to distinguish PendingApproval vs Invalid should use
        /// <see cref="TryGrantDetailed"/>.
        /// </summary>
        public static bool TryGrant(string skillName, string token, string argsJson)
            => TryGrantDetailed(skillName, token, argsJson) == GrantOutcome.Granted;

        /// <summary>
        /// Like <see cref="TryGrant"/> but returns a detailed outcome so callers can map
        /// PendingApproval to GRANT_PENDING_APPROVAL and Invalid to INVALID_TOKEN.
        /// </summary>
        public static GrantOutcome TryGrantDetailed(string skillName, string token, string argsJson)
        {
            if (string.IsNullOrWhiteSpace(token)) return GrantOutcome.Invalid;
            if (!_grants.TryGetValue(token, out var entry)) return GrantOutcome.Invalid;

            if (DateTime.UtcNow > entry.ExpiresAtUtc)
            {
                _grants.TryRemove(token, out _);
                RaiseChanged();
                return GrantOutcome.Invalid;
            }
            if (!string.Equals(entry.SkillName, skillName ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                return GrantOutcome.Invalid;
            if (!string.Equals(entry.ArgsHash, HashArgs(argsJson), StringComparison.Ordinal))
                return GrantOutcome.Invalid;

            if (entry.Channel == ApprovalChannel.Panel && !entry.ApprovedByPanel)
                return GrantOutcome.PendingApproval;

            // Granted — move skill into permanent allow list and free the token slot.
            _grants.TryRemove(token, out _);
            int tokenAgeSec = (int)Math.Max(0, (DateTime.UtcNow - entry.IssuedAtUtc).TotalSeconds);
            AddGranted(entry.SkillName);
            SkillsAuditLog.Append("grant", new
            {
                skill = entry.SkillName,
                token,
                channel = ChannelToWire(entry.Channel),
                tokenAgeSec,
            });
            RaiseChanged();
            return GrantOutcome.Granted;
        }

        /// <summary>
        /// Panel-side approve. Adds the skill to the permanent grant list and removes the
        /// pending token entry immediately so banner / drawer UI reflect "no pending" state
        /// the next tick. The AI's follow-up re-call of the original skill succeeds via
        /// <see cref="GrantedSkills"/> lookup — no token replay is needed.
        /// </summary>
        public static bool Approve(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return false;
            if (!_grants.TryGetValue(token, out var entry)) return false;
            if (DateTime.UtcNow > entry.ExpiresAtUtc)
            {
                _grants.TryRemove(token, out _);
                RaiseChanged();
                return false;
            }
            AddGranted(entry.SkillName);
            SkillsAuditLog.Append("approve", new { skill = entry.SkillName, token, source = "panel" });
            // Approve 已经永久授权该 skill，pending entry 的使命结束；不删的话 banner 会一直显示。
            _grants.TryRemove(token, out _);
            RaiseChanged();
            return true;
        }

        /// <summary>Panel-side deny. Removes the pending entry without granting.</summary>
        public static bool Deny(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return false;
            if (!_grants.TryRemove(token, out var entry)) return false;
            SkillsAuditLog.Append("deny", new { skill = entry.SkillName, token, source = "panel" });
            RaiseChanged();
            return true;
        }

        /// <summary>Revoke a single skill from the permanent grant list.</summary>
        public static void Revoke(string skillName)
        {
            if (string.IsNullOrWhiteSpace(skillName)) return;
            EnsureGrantedLoaded();
            bool removed;
            lock (_grantedLock)
            {
                removed = _granted.Remove(skillName);
                if (removed) SaveGrantedUnlocked();
            }
            if (removed)
            {
                SkillsAuditLog.Append("revoke", new { skill = skillName, source = "panel" });
                RaiseChanged();
            }
        }

        /// <summary>Revoke all permanent grants.</summary>
        public static void RevokeAll()
        {
            EnsureGrantedLoaded();
            int count;
            lock (_grantedLock)
            {
                count = _granted.Count;
                _granted.Clear();
                if (count > 0) SaveGrantedUnlocked();
            }
            if (count > 0)
            {
                SkillsAuditLog.Append("revoke_all", new { count, source = "panel" });
                RaiseChanged();
            }
        }

        // ===== Internal (called from SkillRouter, accepts internal SkillInfo) =====

        /// <summary>
        /// Decide whether a skill may execute under the current operating mode + grant state.
        /// Caller (SkillRouter) translates the result into an error response or continues.
        /// </summary>
        internal static AccessResult CheckAccess(SkillRouter.SkillInfo skill)
        {
            if (skill == null) return AccessResult.Allowed;
            var mode = CurrentMode;

            if (mode == SkillsOperatingMode.Bypass)
                return AccessResult.Allowed;

            if (IsForbiddenInSemi(skill))
                return AccessResult.Forbidden;

            if (mode == SkillsOperatingMode.Auto)
                return AccessResult.Allowed;

            // Approval
            if (skill.Mode == SkillMode.SemiAuto) return AccessResult.Allowed;

            EnsureGrantedLoaded();
            lock (_grantedLock)
            {
                if (_granted.Contains(skill.Name)) return AccessResult.Allowed;
            }
            return AccessResult.NeedsGrant;
        }

        /// <summary>
        /// True if the skill must be blocked outside Bypass mode. Implementation matches
        /// plan section 8 — metadata-driven judgement with a tiny explicit override list.
        /// </summary>
        internal static bool IsForbiddenInSemi(SkillRouter.SkillInfo s)
        {
            if (s == null) return false;
            return s.Operation.HasFlag(SkillOperation.Delete)
                || s.MayEnterPlayMode
                || s.MayTriggerReload
                || string.Equals(s.RiskLevel, "high", StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(s.Name) && _explicitNeverList.Contains(s.Name));
        }

        /// <summary>Wire string for the operating mode ("approval"|"auto"|"bypass").</summary>
        internal static string ModeToWire(SkillsOperatingMode mode) => mode.ToString().ToLowerInvariant();

        /// <summary>Wire string for the approval channel ("dialog"|"panel").</summary>
        internal static string ChannelToWire(ApprovalChannel channel) => channel.ToString().ToLowerInvariant();

        /// <summary>Wire string for a SkillMode ("semi"|"full"). Used by /skills manifest.</summary>
        internal static string SkillModeToWire(SkillMode mode) =>
            mode == SkillMode.SemiAuto ? "semi" : "full";

        /// <summary>Test-only: clear all state (granted, pending, prefs) to a clean slate.</summary>
        internal static void ResetForTests()
        {
            _grants.Clear();
            lock (_grantedLock)
            {
                _granted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                SaveGrantedUnlocked();
            }
            EditorPrefs.DeleteKey(PrefKeyMode);
            EditorPrefs.DeleteKey(PrefKeyPanelApproval);
            EditorPrefs.DeleteKey(PrefKeyGranted);
            RaiseChanged();
        }

        /// <summary>Look up a pending grant entry by token (internal — used by SkillRouter to surface argsSummary).</summary>
        internal static GrantRequest PeekPending(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            return _grants.TryGetValue(token, out var entry) ? ToPublic(entry) : null;
        }

        /// <summary>Test-only: introspect a pending entry by token.</summary>
        internal static GrantRequest PeekPendingForTests(string token) => PeekPending(token);

        // ===== Helpers =====

        private static GrantRequest ToPublic(GrantEntry e) => new GrantRequest
        {
            Token = e.Token,
            SkillName = e.SkillName,
            ArgsSummary = e.ArgsSummary,
            ExpiresAtUtc = e.ExpiresAtUtc,
            ApprovedByPanel = e.ApprovedByPanel,
            Channel = ChannelToWire(e.Channel),
        };

        private static void RaiseChanged()
        {
            try { OnChanged?.Invoke(); }
            catch (Exception ex) { SkillsLogger.LogWarning($"ModeManager OnChanged handler threw: {ex.Message}"); }
        }

        private static void EnsureGrantedLoaded()
        {
            if (_granted != null) return;
            lock (_grantedLock)
            {
                if (_granted != null) return;
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var raw = EditorPrefs.GetString(PrefKeyGranted, string.Empty);
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    try
                    {
                        var arr = JArray.Parse(raw);
                        foreach (var t in arr)
                        {
                            var s = t?.ToString();
                            if (!string.IsNullOrWhiteSpace(s)) set.Add(s);
                        }
                    }
                    catch
                    {
                        // Treat malformed JSON as empty — never crash the editor on a corrupt pref.
                    }
                }
                _granted = set;
            }
        }

        private static void AddGranted(string skillName)
        {
            if (string.IsNullOrWhiteSpace(skillName)) return;
            EnsureGrantedLoaded();
            lock (_grantedLock)
            {
                if (_granted.Add(skillName))
                    SaveGrantedUnlocked();
            }
        }

        private static void SaveGrantedUnlocked()
        {
            var arr = new JArray();
            foreach (var s in _granted.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                arr.Add(s);
            EditorPrefs.SetString(PrefKeyGranted, arr.ToString(Formatting.None));
        }

        private static void CleanupExpired()
        {
            var nowUtc = DateTime.UtcNow;
            bool any = false;
            foreach (var kv in _grants)
            {
                if (nowUtc > kv.Value.ExpiresAtUtc && _grants.TryRemove(kv.Key, out _))
                    any = true;
            }
            if (any) RaiseChanged();
        }

        private static void EnforceCapacity()
        {
            if (_grants.Count < MaxLiveGrants) return;
            foreach (var key in _grants.Keys)
            {
                if (_grants.Count < MaxLiveGrants) break;
                _grants.TryRemove(key, out _);
            }
        }

        private static string GenerateToken()
        {
            var bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static string HashArgs(string argsJson)
        {
            var normalized = (argsJson ?? string.Empty).Trim();
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// Produce a short human-readable summary of args for the panel and audit log.
        /// Keeps top-level scalar key=value pairs and replaces nested objects with "{...}".
        /// </summary>
        private static string SummarizeArgs(string argsJson)
        {
            if (string.IsNullOrWhiteSpace(argsJson)) return string.Empty;
            try
            {
                var obj = JObject.Parse(argsJson);
                var parts = new List<string>();
                foreach (var prop in obj.Properties())
                {
                    string val;
                    switch (prop.Value.Type)
                    {
                        case JTokenType.Object: val = "{...}"; break;
                        case JTokenType.Array:  val = $"[{((JArray)prop.Value).Count}]"; break;
                        case JTokenType.String: val = prop.Value.ToString(); break;
                        default: val = prop.Value.ToString(Formatting.None); break;
                    }
                    if (val.Length > 32) val = val.Substring(0, 29) + "...";
                    parts.Add($"{prop.Name}={val}");
                    if (parts.Count >= 6) break;
                }
                var joined = string.Join(", ", parts);
                if (joined.Length > MaxArgsSummaryChars)
                    joined = joined.Substring(0, MaxArgsSummaryChars - 3) + "...";
                return joined;
            }
            catch
            {
                var s = argsJson.Trim();
                return s.Length > MaxArgsSummaryChars ? s.Substring(0, MaxArgsSummaryChars - 3) + "..." : s;
            }
        }

        /// <summary>
        /// Pre-v1.9 install marker. Any of these global UnitySkills_* prefs means the user
        /// was running the package before the mode system existed → default to Bypass so
        /// the upgrade is behavior-neutral.
        /// </summary>
        private static bool IsExistingInstall()
        {
            return EditorPrefs.HasKey("UnitySkills_RequireConfirmation")
                || EditorPrefs.HasKey("UnitySkills_PreferredPort")
                || EditorPrefs.HasKey("UnitySkills_LogLevel")
                || EditorPrefs.HasKey("UnitySkills_Language")
                || EditorPrefs.HasKey("UnitySkills_RequestTimeoutMinutes")
                || EditorPrefs.HasKey("UnitySkills_KeepAliveIntervalSeconds")
                || EditorPrefs.HasKey("UnitySkills_AutoInstallPackagesOnStartup");
        }
    }
}
