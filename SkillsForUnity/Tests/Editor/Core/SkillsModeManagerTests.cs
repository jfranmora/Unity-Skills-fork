using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Unit tests for the v1.9 Skill mode permission system (plan section 11).
    ///
    /// Covers three operating modes (Approval / Auto / Bypass), two approval channels
    /// (Dialog / Panel), auto NeverInSemi judgement, grant token lifecycle, EditorPrefs
    /// persistence and the upgrade-compat rule (existing install → Bypass).
    ///
    /// Side-effects: every test SetUp wipes UnitySkills_* EditorPrefs and resets the
    /// in-memory grant table + on-disk audit log. Legacy install marker keys are
    /// cleared but NOT restored — if a developer ran the production package on this
    /// machine before, the OneTimeSetUp warning lists the keys that get wiped.
    /// </summary>
    [TestFixture]
    public class SkillsModeManagerTests
    {
        // Pre-v1.9 EditorPrefs keys that mark an "existing install" (plan section 10
        // / SkillsModeManager.IsExistingInstall). Presence of any of these flips the
        // default mode from Approval (fresh install) to Bypass (upgrade-compat).
        private static readonly string[] LegacyInstallKeys =
        {
            "UnitySkills_RequireConfirmation",
            "UnitySkills_PreferredPort",
            "UnitySkills_LogLevel",
            "UnitySkills_Language",
            "UnitySkills_RequestTimeoutMinutes",
            "UnitySkills_KeepAliveIntervalSeconds",
            "UnitySkills_AutoInstallPackagesOnStartup",
        };

        private const string PrefKeyMode = "UnitySkills_OperatingMode";

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var existing = LegacyInstallKeys.Where(EditorPrefs.HasKey).ToList();
            if (existing.Count > 0)
            {
                UnityEngine.Debug.LogWarning(
                    "[SkillsModeManagerTests] Legacy UnitySkills_* prefs detected. " +
                    "Tests in this fixture clear them and they will NOT be restored: "
                    + string.Join(", ", existing));
            }
        }

        [SetUp]
        public void SetUp()
        {
            // Force IsExistingInstall() == false so the default mode getter returns
            // Approval unless a test explicitly opts back into "old install" state.
            foreach (var k in LegacyInstallKeys) EditorPrefs.DeleteKey(k);
            SkillsModeManager.ResetForTests();
            SkillsAuditLog.ResetForTests();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var k in LegacyInstallKeys) EditorPrefs.DeleteKey(k);
            SkillsModeManager.ResetForTests();
            SkillsAuditLog.ResetForTests();
        }

        // ─────────────────────────────────────────────────────────────────
        //  helpers
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Build a SkillInfo with only the fields CheckAccess / IsForbiddenInSemi read.
        /// All other fields (Method, Parameters, etc.) are intentionally null because
        /// the mode manager never touches them.
        /// </summary>
        private static SkillRouter.SkillInfo MakeSkill(
            string name,
            SkillMode mode = SkillMode.FullAuto,
            SkillOperation op = SkillOperation.Modify,
            string risk = "low",
            bool mayEnterPlayMode = false,
            bool mayTriggerReload = false)
        {
            return new SkillRouter.SkillInfo
            {
                Name = name,
                Mode = mode,
                Operation = op,
                RiskLevel = risk,
                MayEnterPlayMode = mayEnterPlayMode,
                MayTriggerReload = mayTriggerReload,
            };
        }

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #1 — Bypass mode allows everything
        // ═════════════════════════════════════════════════════════════════

        [Test]
        public void CheckAccess_BypassMode_AnySkill_AlwaysAllowed()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;

            // Plain SemiAuto / FullAuto — trivially allowed.
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("safe", SkillMode.SemiAuto)));
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("normal")));

            // Every metadata flavour that IsForbiddenInSemi would normally trip on —
            // Bypass mode bypasses the check entirely.
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("del", op: SkillOperation.Delete)));
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("play", mayEnterPlayMode: true)));
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("reload", mayTriggerReload: true)));
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("high_risk", risk: "high")));
            // explicit "never in semi" list (e.g. scene_clear)
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("scene_clear")));
        }

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #2 — Auto mode allows Semi & Full
        // ═════════════════════════════════════════════════════════════════

        [Test]
        public void CheckAccess_AutoMode_SemiAutoAndFullAuto_Allowed()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Auto;

            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("semi_one", SkillMode.SemiAuto)));
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("full_one", SkillMode.FullAuto)));
        }

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #3 — Auto mode still blocks auto-judged NeverInSemi
        // ═════════════════════════════════════════════════════════════════

        [Test]
        public void CheckAccess_AutoMode_NeverInSemiSkill_Forbidden()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Auto;

            Assert.AreEqual(SkillsModeManager.AccessResult.Forbidden,
                SkillsModeManager.CheckAccess(MakeSkill("delete_thing", op: SkillOperation.Delete)));
        }

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #4 — Approval + SemiAuto bypasses the grant gate
        // ═════════════════════════════════════════════════════════════════

        [Test]
        public void CheckAccess_ApprovalMode_SemiAutoSkill_Allowed()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;

            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("preview_thing", SkillMode.SemiAuto)));
        }

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #5 — Approval + FullAuto without grant → NeedsGrant
        // ═════════════════════════════════════════════════════════════════

        [Test]
        public void CheckAccess_ApprovalMode_FullAutoUngranted_NeedsGrant()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;

            Assert.AreEqual(SkillsModeManager.AccessResult.NeedsGrant,
                SkillsModeManager.CheckAccess(MakeSkill("smart_layout")));
        }

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #6 — Approval + Dialog grant + recheck → Allowed
        // ═════════════════════════════════════════════════════════════════

        [Test]
        public void Approval_DialogChannel_GrantThenRecheck_Allowed()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
            SkillsModeManager.PanelApprovalRequired = false; // explicit, default

            const string skillName = "smart_layout";
            const string args = "{\"target\":\"Cube\"}";

            var (token, ttl, channel) = SkillsModeManager.IssueGrantRequest(skillName, args);
            Assert.AreEqual(SkillsModeManager.ApprovalChannel.Dialog, channel);
            Assert.Greater(ttl, 0, "TTL should be a positive number of seconds");
            Assert.IsFalse(string.IsNullOrWhiteSpace(token), "Token must be non-empty");

            Assert.IsTrue(SkillsModeManager.TryGrant(skillName, token, args));

            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill(skillName)));
            CollectionAssert.Contains(SkillsModeManager.GrantedSkills, skillName);
        }

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #7 — Approval + Panel ON + no approval yet → PendingApproval
        // ═════════════════════════════════════════════════════════════════

        [Test]
        public void Approval_PanelChannel_GrantBeforeApprove_ReturnsPendingApproval()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
            SkillsModeManager.PanelApprovalRequired = true;

            const string skillName = "smart_layout";
            const string args = "{\"target\":\"Cube\"}";

            var (token, _, channel) = SkillsModeManager.IssueGrantRequest(skillName, args);
            Assert.AreEqual(SkillsModeManager.ApprovalChannel.Panel, channel);

            // AI re-plays the token before the user clicks Approve on the panel.
            Assert.AreEqual(GrantOutcome.PendingApproval,
                SkillsModeManager.TryGrantDetailed(skillName, token, args));
            CollectionAssert.DoesNotContain(SkillsModeManager.GrantedSkills, skillName);

            // The entry is still alive in the panel pending list.
            var pending = SkillsModeManager.PeekPendingForTests(token);
            Assert.IsNotNull(pending);
            Assert.AreEqual(skillName, pending.SkillName);
            Assert.IsFalse(pending.ApprovedByPanel);
        }

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #8 — Approval + Panel Approve + grant + recheck → Allowed
        // ═════════════════════════════════════════════════════════════════

        [Test]
        public void Approval_PanelChannel_ApproveThenGrant_RecheckAllowed()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
            SkillsModeManager.PanelApprovalRequired = true;

            const string skillName = "smart_layout";
            const string args = "{\"target\":\"Cube\"}";

            var (token, _, _) = SkillsModeManager.IssueGrantRequest(skillName, args);

            Assert.IsTrue(SkillsModeManager.Approve(token));
            // Per α-Core protocol the skill enters GrantedSkills immediately on Approve
            // so the AI's re-call of the original skill succeeds.
            CollectionAssert.Contains(SkillsModeManager.GrantedSkills, skillName);

            // A racing TryGrant from the AI side must still succeed (token kept alive).
            Assert.AreEqual(GrantOutcome.Granted,
                SkillsModeManager.TryGrantDetailed(skillName, token, args));

            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill(skillName)));
        }

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #9 — Panel Deny + grant → false
        // ═════════════════════════════════════════════════════════════════

        [Test]
        public void Approval_PanelChannel_DenyThenGrant_ReturnsFalse()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
            SkillsModeManager.PanelApprovalRequired = true;

            const string skillName = "smart_layout";
            const string args = "{\"x\":1}";

            var (token, _, _) = SkillsModeManager.IssueGrantRequest(skillName, args);

            Assert.IsTrue(SkillsModeManager.Deny(token));

            // Token entry is gone, grant must fail and the skill stays ungranted.
            Assert.IsFalse(SkillsModeManager.TryGrant(skillName, token, args));
            Assert.AreEqual(GrantOutcome.Invalid,
                SkillsModeManager.TryGrantDetailed(skillName, token, args));
            CollectionAssert.DoesNotContain(SkillsModeManager.GrantedSkills, skillName);
            Assert.IsNull(SkillsModeManager.PeekPendingForTests(token));
        }

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #10 — Approval + NeverInSemi → Forbidden (no grant route)
        // ═════════════════════════════════════════════════════════════════

        [Test]
        public void CheckAccess_ApprovalMode_NeverInSemiSkill_Forbidden()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;

            Assert.AreEqual(SkillsModeManager.AccessResult.Forbidden,
                SkillsModeManager.CheckAccess(MakeSkill("delete_thing", op: SkillOperation.Delete)));
            Assert.AreEqual(SkillsModeManager.AccessResult.Forbidden,
                SkillsModeManager.CheckAccess(MakeSkill("scene_clear")));
        }

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #11 — invalid token paths
        // ═════════════════════════════════════════════════════════════════

        [Test]
        public void TryGrant_InvalidToken_ReturnsFalseAndInvalid()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;

            // Never-issued token.
            Assert.IsFalse(SkillsModeManager.TryGrant("any_skill", "bogus_token_xxx", "{}"));
            Assert.AreEqual(GrantOutcome.Invalid,
                SkillsModeManager.TryGrantDetailed("any_skill", "bogus_token_xxx", "{}"));

            // Empty / whitespace token.
            Assert.AreEqual(GrantOutcome.Invalid,
                SkillsModeManager.TryGrantDetailed("any_skill", "", "{}"));
            Assert.AreEqual(GrantOutcome.Invalid,
                SkillsModeManager.TryGrantDetailed("any_skill", "   ", "{}"));

            // Valid token but mismatched args → Invalid.
            const string skill = "smart_layout";
            var (token, _, _) = SkillsModeManager.IssueGrantRequest(skill, "{\"a\":1}");
            Assert.AreEqual(GrantOutcome.Invalid,
                SkillsModeManager.TryGrantDetailed(skill, token, "{\"a\":2}"));
        }

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #12 — Revoke after grant → back to NeedsGrant
        // ═════════════════════════════════════════════════════════════════

        [Test]
        public void Revoke_AfterGrant_CheckAccessReturnsNeedsGrant()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
            const string skillName = "smart_layout";
            const string args = "{}";

            var (token, _, _) = SkillsModeManager.IssueGrantRequest(skillName, args);
            Assert.IsTrue(SkillsModeManager.TryGrant(skillName, token, args));
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill(skillName)));

            SkillsModeManager.Revoke(skillName);

            CollectionAssert.DoesNotContain(SkillsModeManager.GrantedSkills, skillName);
            Assert.AreEqual(SkillsModeManager.AccessResult.NeedsGrant,
                SkillsModeManager.CheckAccess(MakeSkill(skillName)));
        }

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #13 — CurrentMode persists via EditorPrefs
        // ═════════════════════════════════════════════════════════════════

        [Test]
        public void CurrentMode_Setter_PersistsToEditorPrefs_AndGetterReadsIt()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Auto;

            // Direct EditorPrefs verification — the setter persisted under PrefKeyMode.
            Assert.IsTrue(EditorPrefs.HasKey(PrefKeyMode));
            Assert.AreEqual("Auto", EditorPrefs.GetString(PrefKeyMode));
            Assert.AreEqual(SkillsOperatingMode.Auto, SkillsModeManager.CurrentMode);

            // Switching writes the new value (overwrite, not append).
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
            Assert.AreEqual("Approval", EditorPrefs.GetString(PrefKeyMode));
            Assert.AreEqual(SkillsOperatingMode.Approval, SkillsModeManager.CurrentMode);
        }

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #14 — IsForbiddenInSemi auto judgement
        // ═════════════════════════════════════════════════════════════════

        [Test]
        public void IsForbiddenInSemi_CoversAllAutoJudgementBranches()
        {
            // Five flavours that MUST be forbidden in Approval / Auto.
            Assert.IsTrue(SkillsModeManager.IsForbiddenInSemi(
                MakeSkill("del", op: SkillOperation.Delete)),
                "SkillOperation.Delete must be forbidden");
            Assert.IsTrue(SkillsModeManager.IsForbiddenInSemi(
                MakeSkill("enter_play", mayEnterPlayMode: true)),
                "MayEnterPlayMode must be forbidden");
            Assert.IsTrue(SkillsModeManager.IsForbiddenInSemi(
                MakeSkill("trigger_reload", mayTriggerReload: true)),
                "MayTriggerReload must be forbidden");
            Assert.IsTrue(SkillsModeManager.IsForbiddenInSemi(
                MakeSkill("hot", risk: "high")),
                "RiskLevel=\"high\" must be forbidden");
            Assert.IsTrue(SkillsModeManager.IsForbiddenInSemi(
                MakeSkill("scene_clear")),
                "Names in the explicit never list must be forbidden");

            // Plain SemiAuto / FullAuto without any flag must NOT be forbidden.
            Assert.IsFalse(SkillsModeManager.IsForbiddenInSemi(
                MakeSkill("plain_semi", SkillMode.SemiAuto)));
            Assert.IsFalse(SkillsModeManager.IsForbiddenInSemi(
                MakeSkill("plain_full", SkillMode.FullAuto)));

            // Combined-flags Operation (Query|Modify) without Delete remains allowed.
            Assert.IsFalse(SkillsModeManager.IsForbiddenInSemi(
                MakeSkill("query_modify", op: SkillOperation.Query | SkillOperation.Modify)));
        }

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #15 — Audit log records grant events
        // ═════════════════════════════════════════════════════════════════

        [Test]
        public void AuditLog_GrantEvent_AppendThenFlushSync_ReadRecentContainsIt()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
            const string skillName = "smart_layout";
            const string args = "{\"x\":1}";

            var (token, _, _) = SkillsModeManager.IssueGrantRequest(skillName, args);
            Assert.IsTrue(SkillsModeManager.TryGrant(skillName, token, args));

            // Append is async; force a flush so ReadRecent sees the line.
            SkillsAuditLog.FlushSync();
            var recent = SkillsAuditLog.ReadRecent(50);

            Assert.IsNotNull(recent);
            Assert.Greater(recent.Count, 0, "Audit log should contain at least one event");

            bool foundGrant = recent
                .OfType<JObject>()
                .Any(j => j["type"]?.ToString() == "grant"
                       && j["skill"]?.ToString() == skillName
                       && j["token"]?.ToString() == token);
            Assert.IsTrue(foundGrant,
                "Expected a 'grant' audit event for skill=" + skillName + " token=" + token);
        }

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #16 — Old install (legacy pref exists) → Bypass default
        // ═════════════════════════════════════════════════════════════════

        [Test]
        public void CurrentMode_OldInstall_NoExplicitMode_DefaultsToBypass()
        {
            // SetUp already cleared every legacy + mode key. Plant just one legacy
            // marker — IsExistingInstall uses HasKey only, value doesn't matter.
            EditorPrefs.SetInt("UnitySkills_PreferredPort", 12345);

            Assert.AreEqual(SkillsOperatingMode.Bypass, SkillsModeManager.CurrentMode);
            // Getter must NOT write PrefKeyMode as a side effect — that would prevent
            // the next upgrade from re-evaluating the default.
            Assert.IsFalse(EditorPrefs.HasKey(PrefKeyMode));
        }

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #17 — Fresh install (no legacy, no explicit) → Approval
        // ═════════════════════════════════════════════════════════════════

        [Test]
        public void CurrentMode_FreshInstall_NoKeys_DefaultsToApproval()
        {
            // SetUp left zero UnitySkills_* keys behind.
            Assert.AreEqual(SkillsOperatingMode.Approval, SkillsModeManager.CurrentMode);
            Assert.IsFalse(EditorPrefs.HasKey(PrefKeyMode));
        }
    }
}
