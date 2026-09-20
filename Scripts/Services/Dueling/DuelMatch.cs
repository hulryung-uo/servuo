using System;
using System.Collections.Generic;
using Server.Items;
using Server.Mobiles;
using Server.Network;

namespace Server.Engines.Dueling
{
    public enum DuelPhase
    {
        Countdown,  // fighters placed, frozen, "begins in N..." messages
        Fighting,   // round live
        RoundOver,  // a round just ended; waiting to reset for the next one
        Finished    // match over (or aborted)
    }

    /// <summary>
    /// One best-of-N match between two players in the fixed arena. Drives itself with a 1-second timer.
    /// All journal output goes through Announce() as plain "[Duel] ..." system messages.
    /// </summary>
    public class DuelMatch
    {
        public static readonly TimeSpan RoundTimeLimit = TimeSpan.FromMinutes(3.0);
        public static readonly TimeSpan OfflineForfeit = TimeSpan.FromSeconds(30.0);
        public const int CountdownSeconds = 5;
        public const int AnnounceRange = 24;

        public PlayerMobile A { get; private set; }
        public PlayerMobile B { get; private set; }
        public int Rounds { get; private set; }
        public DuelRules Rules { get; private set; }

        public int Round { get; private set; }
        public int ScoreA { get; private set; }
        public int ScoreB { get; private set; }
        public DuelPhase Phase { get; private set; }

        private Timer m_Timer;
        private int m_Countdown;
        private DateTime m_RoundStart;
        private int m_HarmfulA, m_HarmfulB; // harmful actions (swings etc.) per fighter this round, for the detail line
        private DateTime m_OfflineSinceA = DateTime.MinValue;
        private DateTime m_OfflineSinceB = DateTime.MinValue;

        public DuelMatch(PlayerMobile a, PlayerMobile b, int rounds, DuelRules rules)
        {
            A = a;
            B = b;
            Rounds = Math.Max(1, rounds);
            Rules = rules ?? DuelRules.Default;
            Phase = DuelPhase.Countdown;
        }

        public bool IsFighter(Mobile m)
        {
            return m != null && (m == A || m == B);
        }

        public PlayerMobile Opponent(Mobile m)
        {
            return m == A ? B : (m == B ? A : null);
        }

        private Point3D MarkOf(Mobile m)
        {
            return m == A ? DuelArena.MarkA : DuelArena.MarkB;
        }

        private Point3D ExitOf(Mobile m)
        {
            return m == A ? DuelArena.ExitA : DuelArena.ExitB;
        }

        private int WinsNeeded { get { return Rounds / 2 + 1; } }

        /// <summary>Called by the region for every harmful action a fighter performs while the round is live.</summary>
        public void NoteHarmful(Mobile harmer)
        {
            if (Phase != DuelPhase.Fighting)
                return;

            if (harmer == A) m_HarmfulA++;
            else if (harmer == B) m_HarmfulB++;
        }

        private static int HpPercent(Mobile m)
        {
            if (m == null || m.Deleted || m.HitsMax <= 0)
                return 0;

            return Math.Max(0, Math.Min(100, m.Hits * 100 / m.HitsMax));
        }

        private void AnnounceRoundDetail()
        {
            Announce(String.Format("[Duel] Round {0} detail: {1} hp {2}%, {3} hp {4}%, attacks {5}/{6}.",
                Round, A.Name, HpPercent(A), B.Name, HpPercent(B), m_HarmfulA, m_HarmfulB));
        }

        public string StatusLine()
        {
            switch (Phase)
            {
                case DuelPhase.Finished:
                    return String.Format("[Duel] Status: finished. {0} {1} - {2} {3}.", A.Name, ScoreA, ScoreB, B.Name);
                case DuelPhase.Fighting:
                    return String.Format("[Duel] Status: round {0} of {1} live ({2} seconds elapsed). {3} {4} - {5} {6}, rules {7}.",
                        Round, Rounds, (int)(DateTime.UtcNow - m_RoundStart).TotalSeconds, A.Name, ScoreA, ScoreB, B.Name, Rules);
                default:
                    return String.Format("[Duel] Status: round {0} of {1} starting. {2} {3} - {4} {5}, rules {6}.",
                        Round, Rounds, A.Name, ScoreA, ScoreB, B.Name, Rules);
            }
        }

        #region Messaging

        /// <summary>Sends a fixed-format system message to both fighters and everyone near the arena, and logs it to the console.</summary>
        public void Announce(string text)
        {
            var seen = new HashSet<Mobile>();

            foreach (Mobile m in new[] { A, B })
            {
                if (m != null && !m.Deleted && seen.Add(m))
                    m.SendMessage(DuelSystem.MessageHue, text);
            }

            var eable = DuelArena.ArenaMap.GetClientsInRange(DuelArena.Center, AnnounceRange);

            foreach (NetState ns in eable)
            {
                Mobile m = ns.Mobile;

                if (m != null && seen.Add(m))
                    m.SendMessage(DuelSystem.MessageHue, text);
            }

            eable.Free();

            Console.WriteLine(text);
        }

        #endregion

        #region Lifecycle

        public void Start()
        {
            Announce(String.Format("[Duel] Start: {0} vs {1}, best of {2}, rules {3}.", A.Name, B.Name, Rounds, Rules));

            DuelArena.EvictOthers(A, B);
            BeginRound();
        }

        private void BeginRound()
        {
            Round++;
            Phase = DuelPhase.Countdown;
            m_Countdown = CountdownSeconds;
            m_OfflineSinceA = m_OfflineSinceB = DateTime.MinValue;
            m_HarmfulA = m_HarmfulB = 0;

            PrepareFighter(A);
            PrepareFighter(B);

            Announce(String.Format("[Duel] Round {0} of {1} begins in {2}...", Round, Rounds, m_Countdown));

            StopTimer();
            m_Timer = Timer.DelayCall(TimeSpan.FromSeconds(1.0), TimeSpan.FromSeconds(1.0), OnTick);
        }

        /// <summary>Resurrect if needed, move to the start mark, full heal, strip disallowed gear, freeze for the countdown.</summary>
        private void PrepareFighter(PlayerMobile m)
        {
            if (m == null || m.Deleted)
                return;

            if (!m.Alive)
                m.Resurrect();

            RemoveDeathRobe(m);

            m.MoveToWorld(MarkOf(m), DuelArena.ArenaMap);
            FullHeal(m);

            foreach (Item item in Rules.EnforceEquipment(m))
                Announce(String.Format("[Duel] {0}: {1} unequipped (rule {2}).", m.Name, ItemName(item), Rules.GetEquipViolation(item)));

            m.Combatant = null;
            m.Frozen = true;
        }

        public static void FullHeal(Mobile m)
        {
            if (m == null || m.Deleted)
                return;

            m.Poison = null;
            m.Paralyzed = false;
            m.Hits = m.HitsMax;
            m.Stam = m.StamMax;
            m.Mana = m.ManaMax;
        }

        private static void RemoveDeathRobe(Mobile m)
        {
            Item robe = m.FindItemOnLayer(Layer.OuterTorso);

            if (robe is DeathRobe)
                robe.Delete();
        }

        private static string ItemName(Item item)
        {
            if (!String.IsNullOrEmpty(item.Name))
                return item.Name;

            return item.GetType().Name.ToLowerInvariant();
        }

        private void Fight()
        {
            Phase = DuelPhase.Fighting;
            m_RoundStart = DateTime.UtcNow;

            A.Frozen = false;
            B.Frozen = false;

            Announce("[Duel] FIGHT!");
        }

        private void OnTick()
        {
            if (Phase == DuelPhase.Finished)
            {
                StopTimer();
                return;
            }

            if (A == null || A.Deleted || B == null || B.Deleted)
            {
                Abort("a fighter no longer exists");
                return;
            }

            // Rules and presence checks apply to both the countdown and the live round.
            if (CheckForfeit(A, ref m_OfflineSinceA) || CheckForfeit(B, ref m_OfflineSinceB))
                return;

            if (Phase == DuelPhase.Countdown)
            {
                m_Countdown--;

                if (m_Countdown > 0)
                    Announce(String.Format("[Duel] Round {0} of {1} begins in {2}...", Round, Rounds, m_Countdown));
                else
                    Fight();
            }
            else if (Phase == DuelPhase.Fighting)
            {
                EnforceRules(A);
                EnforceRules(B);

                if (DateTime.UtcNow - m_RoundStart >= RoundTimeLimit)
                    EndRoundDraw();
            }
        }

        private void EnforceRules(PlayerMobile m)
        {
            foreach (Item item in Rules.EnforceEquipment(m))
                Announce(String.Format("[Duel] {0}: {1} unequipped (rule {2}).", m.Name, ItemName(item), Rules.GetEquipViolation(item)));
        }

        /// <summary>Forfeits the round if the fighter left the arena or has been offline too long. Returns true if the round ended.</summary>
        private bool CheckForfeit(PlayerMobile m, ref DateTime offlineSince)
        {
            if (Phase != DuelPhase.Countdown && Phase != DuelPhase.Fighting)
                return false;

            if (!DuelArena.Contains(m))
            {
                EndRound(Opponent(m), m, "forfeit: left the arena");
                return true;
            }

            if (m.NetState == null)
            {
                if (offlineSince == DateTime.MinValue)
                    offlineSince = DateTime.UtcNow;
                else if (DateTime.UtcNow - offlineSince >= OfflineForfeit)
                {
                    EndRound(Opponent(m), m, "forfeit: disconnected");
                    return true;
                }
            }
            else
            {
                offlineSince = DateTime.MinValue;
            }

            return false;
        }

        /// <summary>Called by the region when someone dies inside the arena.</summary>
        public void HandleDeath(Mobile m)
        {
            if (!IsFighter(m))
                return;

            if (Phase != DuelPhase.Fighting && Phase != DuelPhase.Countdown)
                return;

            EndRound(Opponent(m), (PlayerMobile)m, null);
        }

        private int ElapsedSeconds()
        {
            if (Phase == DuelPhase.Countdown || m_RoundStart == DateTime.MinValue)
                return 0;

            return (int)(DateTime.UtcNow - m_RoundStart).TotalSeconds;
        }

        private int m_LastRoundSeconds;

        private void EndRound(PlayerMobile winner, PlayerMobile loser, string reason)
        {
            int seconds = m_LastRoundSeconds = ElapsedSeconds();

            Phase = DuelPhase.RoundOver;

            if (winner == A) ScoreA++; else ScoreB++;

            if (reason == null)
                Announce(String.Format("[Duel] Round {0}: {1} defeats {2} (hp {3}%, {4} seconds).", Round, winner.Name, loser.Name, HpPercent(winner), seconds));
            else
                Announce(String.Format("[Duel] Round {0}: {1} defeats {2} ({3}, {4} seconds).", Round, winner.Name, loser.Name, reason, seconds));

            AnnounceRoundDetail();
            DuelSystem.RecordRound(winner, loser);
            AfterRound();
        }

        private void EndRoundDraw()
        {
            int seconds = m_LastRoundSeconds = ElapsedSeconds();

            Phase = DuelPhase.RoundOver;

            Announce(String.Format("[Duel] Round {0}: Draw: time limit ({1} seconds).", Round, seconds));
            AnnounceRoundDetail();
            AfterRound();
        }

        private void AfterRound()
        {
            StopTimer();

            Announce(String.Format("[Duel] Score: {0} {1} - {2} {3} ({4} seconds).", A.Name, ScoreA, ScoreB, B.Name, m_LastRoundSeconds));

            A.Frozen = false;
            B.Frozen = false;

            bool decided = ScoreA >= WinsNeeded || ScoreB >= WinsNeeded || Round >= Rounds;

            // Give the death/kill packets a moment to land before resurrecting and moving people around.
            m_Timer = Timer.DelayCall(TimeSpan.FromSeconds(1.0), () =>
            {
                if (Phase == DuelPhase.Finished)
                    return;

                if (decided)
                    Finish();
                else
                    BeginRound();
            });
        }

        private void Finish()
        {
            Phase = DuelPhase.Finished;
            StopTimer();

            PlayerMobile winner = ScoreA > ScoreB ? A : (ScoreB > ScoreA ? B : null);

            if (winner != null)
                Announce(String.Format("[Duel] Match: {0} {1} - {2} {3}. {4} wins.", A.Name, ScoreA, ScoreB, B.Name, winner.Name));
            else
                Announce(String.Format("[Duel] Match: {0} {1} - {2} {3}. Draw.", A.Name, ScoreA, ScoreB, B.Name));

            DuelSystem.RecordMatch(A, B, winner);

            ReleaseFighter(A);
            ReleaseFighter(B);

            DuelSystem.OnMatchFinished(this);
        }

        /// <summary>Staff abort or a fighter vanished: heal everyone, put them in the lobby, no stats recorded.</summary>
        public void Abort(string reason)
        {
            if (Phase == DuelPhase.Finished)
                return;

            Phase = DuelPhase.Finished;
            StopTimer();

            Announce(String.Format("[Duel] Match aborted: {0}.", reason));

            ReleaseFighter(A);
            ReleaseFighter(B);

            DuelSystem.OnMatchFinished(this);
        }

        /// <summary>Resurrects/heals the fighter and moves them to the lobby spot outside the fence.</summary>
        private void ReleaseFighter(PlayerMobile m)
        {
            if (m == null || m.Deleted)
                return;

            m.Frozen = false;

            if (!m.Alive)
                m.Resurrect();

            RemoveDeathRobe(m);
            FullHeal(m);

            m.Combatant = null;
            m.Warmode = false;

            if (m.Map == DuelArena.ArenaMap && DuelArena.Bounds.Contains(m.Location))
                m.MoveToWorld(ExitOf(m), DuelArena.ArenaMap);
        }

        private void StopTimer()
        {
            if (m_Timer != null)
            {
                m_Timer.Stop();
                m_Timer = null;
            }
        }

        #endregion
    }
}
