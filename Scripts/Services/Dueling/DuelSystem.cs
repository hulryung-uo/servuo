using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Server.Items;
using Server.Mobiles;
using Server.Network;

namespace Server.Engines.Dueling
{
    public class DuelRecord
    {
        public int MatchWins, MatchLosses, MatchDraws, RoundWins, RoundLosses;

        public string Describe(string name)
        {
            return String.Format("[Duel] Stats for {0}: matches {1}W-{2}L-{3}D, rounds {4}W-{5}L.", name, MatchWins, MatchLosses, MatchDraws, RoundWins, RoundLosses);
        }
    }

    public class DuelChallenge
    {
        public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60.0);

        public PlayerMobile Challenger { get; private set; }
        public PlayerMobile Target { get; private set; }
        public int Rounds { get; private set; }
        public DuelRules Rules { get; private set; }
        public DateTime Expires { get; private set; }

        public DuelChallenge(PlayerMobile challenger, PlayerMobile target, int rounds, DuelRules rules)
        {
            Challenger = challenger;
            Target = target;
            Rounds = rounds;
            Rules = rules;
            Expires = DateTime.UtcNow + Lifetime;
        }

        public bool Expired { get { return DateTime.UtcNow >= Expires; } }
    }

    /// <summary>
    /// T2A-friendly player duel system: speech commands, several fixed arenas (see DuelArena) running independent
    /// best-of-N matches at once, no corpse looting / murder counts for fighters, plain "[Duel] ..." journal lines.
    /// </summary>
    public static class DuelSystem
    {
        public const int MessageHue = 0x35;
        public const int DefaultRounds = 3;
        public const int MaxRounds = 15;

        public static readonly string SavePath = Path.Combine("Saves", "Dueling.bin");

        public static readonly List<DuelMatch> Matches = new List<DuelMatch>();

        private static readonly Dictionary<Mobile, DuelChallenge> m_Pending = new Dictionary<Mobile, DuelChallenge>(); // keyed by challenged player
        private static readonly Dictionary<Mobile, DuelRecord> m_Stats = new Dictionary<Mobile, DuelRecord>();

        public static void Configure()
        {
            EventSink.WorldSave += OnWorldSave;
            EventSink.WorldLoad += OnWorldLoad;
            EventSink.PlayerDeath += OnPlayerDeath;
        }

        public static void Initialize()
        {
            DuelArena.Setup();
            DuelArena.EnsureAllBuilt();

            DuelCommands.Register();

            Timer.DelayCall(TimeSpan.FromSeconds(5.0), TimeSpan.FromSeconds(5.0), ExpireChallenges);
        }

        #region Hooks used from core scripts (PlayerMobile, Notoriety, Bandage)

        /// <summary>The unfinished match the mobile is fighting in, or null.</summary>
        public static DuelMatch FindMatchOf(Mobile m)
        {
            if (m == null)
                return null;

            for (int i = 0; i < Matches.Count; i++)
            {
                DuelMatch match = Matches[i];

                if (match.Phase != DuelPhase.Finished && match.IsFighter(m))
                    return match;
            }

            return null;
        }

        /// <summary>Fighters (and anyone dying inside an arena) keep every item; nothing goes to the corpse.</summary>
        public static bool KeepsItemsOnDeath(Mobile m)
        {
            if (m == null)
                return false;

            return FindMatchOf(m) != null || DuelArena.Find(m) != null;
        }

        private static readonly Dictionary<Mobile, Item> m_OuterTorso = new Dictionary<Mobile, Item>();

        /// <summary>
        /// Death hook for worn items. Everything stays equipped except the OuterTorso piece, which is parked in the
        /// backpack so the death shroud can take its layer; RestoreOuterTorso puts it back after resurrection.
        /// </summary>
        public static bool TryGetDeathMoveResult(Mobile m, Item item, out DeathMoveResult result)
        {
            result = DeathMoveResult.RemainEquiped;

            if (!KeepsItemsOnDeath(m))
                return false;

            if (item.Layer == Layer.OuterTorso)
            {
                m_OuterTorso[m] = item;
                result = DeathMoveResult.MoveToBackpack;
            }

            return true;
        }

        public static void RestoreOuterTorso(Mobile m)
        {
            Item item;

            if (m == null || !m_OuterTorso.TryGetValue(m, out item))
                return;

            m_OuterTorso.Remove(m);

            if (item != null && !item.Deleted && m.Alive && item.Parent == m.Backpack && m.FindItemOnLayer(Layer.OuterTorso) == null)
                m.EquipItem(item);
        }

        /// <summary>Opposing fighters are "Enemy" (orange) to each other for the whole match, so no criminal flags or murder counts.</summary>
        public static bool IsEnemy(Mobile source, Mobile target)
        {
            if (source == target)
                return false;

            var match = FindMatchOf(source);

            return match != null && match.IsFighter(target);
        }

        /// <summary>Equip check hook: refuse gear the current rules forbid while the wearer is a fighter in a match.</summary>
        public static bool AllowEquip(Mobile m, Item item)
        {
            var match = FindMatchOf(m);

            if (match == null)
                return true;

            string violation = match.Rules.GetEquipViolation(item);

            if (violation == null)
                return true;

            m.SendMessage(MessageHue, String.Format("[Duel] You cannot equip that in this duel (rule {0}).", violation));
            return false;
        }

        public static bool AllowBandage(Mobile healer, Mobile patient)
        {
            if (healer == null)
                return true;

            if (patient != null && healer != patient && FindMatchOf(patient) != null)
            {
                healer.SendMessage(MessageHue, "[Duel] You cannot heal a duelist during a match.");
                return false;
            }

            var match = FindMatchOf(healer);

            if (match != null && match.Rules.NoBandage)
            {
                healer.SendMessage(MessageHue, "[Duel] Bandages are not allowed in this duel.");
                return false;
            }

            return true;
        }

        private static void OnPlayerDeath(PlayerDeathEventArgs e)
        {
            Mobile m = e.Mobile;

            if (!KeepsItemsOnDeath(m))
                return;

            var corpse = e.Corpse as Corpse;

            if (corpse == null || corpse.Deleted)
                return;

            // Should be empty thanks to the PlayerMobile hook; anything that slipped through goes straight back to the player.
            foreach (Item item in corpse.Items.ToList())
                m.AddToBackpack(item);

            corpse.Delete();
        }

        #endregion

        #region Challenges and matches

        public static PlayerMobile FindOnlinePlayer(string name)
        {
            return FindOnlinePlayer(name, null);
        }

        /// <summary>
        /// Resolves a fighter by serial ("0x880") or by name. Names are not unique, so among online namesakes
        /// the one not already in a match and closest to 'near' (the issuer) wins.
        /// </summary>
        public static PlayerMobile FindOnlinePlayer(string name, Mobile near)
        {
            if (String.IsNullOrWhiteSpace(name))
                return null;

            if (name.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                int serial;

                if (Int32.TryParse(name.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out serial))
                {
                    var bySerial = World.FindMobile(serial) as PlayerMobile;

                    return bySerial != null && !bySerial.Deleted && bySerial.NetState != null ? bySerial : null;
                }
            }

            PlayerMobile best = null;
            bool bestFree = false;
            int bestDistance = Int32.MaxValue;

            foreach (NetState ns in NetState.Instances)
            {
                var pm = ns.Mobile as PlayerMobile;

                if (pm == null || pm.Deleted || !Insensitive.Equals(pm.Name, name))
                    continue;

                bool free = FindMatchOf(pm) == null;
                int distance = (near != null && near.Map == pm.Map) ? (int)near.GetDistanceToSqrt(pm) : Int32.MaxValue - 1;

                if (best == null || (free && !bestFree) || (free == bestFree && distance < bestDistance))
                {
                    best = pm;
                    bestFree = free;
                    bestDistance = distance;
                }
            }

            return best;
        }

        /// <summary>Returns null if the player may enter a match right now, otherwise the reason (already prefixed).</summary>
        public static string CheckAvailable(PlayerMobile pm, DuelRules rules)
        {
            if (pm == null || pm.Deleted || pm.NetState == null)
                return "[Duel] That player is not online.";

            if (FindMatchOf(pm) != null)
                return String.Format("[Duel] {0} is already in a match.", pm.Name);

            if (DuelArena.FindFree() == null)
                return "[Duel] All arenas are busy.";

            string reason;

            if (rules != null && !rules.CheckSkills(pm, out reason))
                return "[Duel] Cannot start: " + reason;

            return null;
        }

        public static void Challenge(PlayerMobile challenger, PlayerMobile target, int rounds, DuelRules rules)
        {
            if (challenger == target)
            {
                challenger.SendMessage(MessageHue, "[Duel] You cannot challenge yourself.");
                return;
            }

            string problem = CheckAvailable(challenger, rules);

            if (problem != null)
            {
                challenger.SendMessage(MessageHue, problem);
                return;
            }

            if (target.NetState == null)
            {
                challenger.SendMessage(MessageHue, String.Format("[Duel] {0} is not online.", target.Name));
                return;
            }

            if (FindMatchOf(target) != null)
            {
                challenger.SendMessage(MessageHue, String.Format("[Duel] {0} is already in a match.", target.Name));
                return;
            }

            // A new challenge to the same player replaces the old one; the challenger's earlier pending challenges are dropped.
            foreach (Mobile key in m_Pending.Where(kv => kv.Value.Challenger == challenger).Select(kv => kv.Key).ToList())
                m_Pending.Remove(key);

            m_Pending[target] = new DuelChallenge(challenger, target, rounds, rules);

            string text = String.Format("[Duel] {0} has challenged {1}: best of {2}, rules {3}. Say [Accept to fight.", challenger.Name, target.Name, rounds, rules);

            challenger.SendMessage(MessageHue, text);
            target.SendMessage(MessageHue, text);
            Console.WriteLine(text);
        }

        public static void Accept(PlayerMobile pm)
        {
            DuelChallenge challenge;

            if (!m_Pending.TryGetValue(pm, out challenge) || challenge.Expired)
            {
                m_Pending.Remove(pm);
                pm.SendMessage(MessageHue, "[Duel] No pending challenge.");
                return;
            }

            PlayerMobile challenger = challenge.Challenger;

            string problem = CheckAvailable(challenger, challenge.Rules);

            if (problem != null)
            {
                pm.SendMessage(MessageHue, problem);
                challenger.SendMessage(MessageHue, problem);
                return;
            }

            problem = CheckAvailable(pm, challenge.Rules);

            if (problem != null)
            {
                pm.SendMessage(MessageHue, problem);
                challenger.SendMessage(MessageHue, problem);
                return;
            }

            m_Pending.Remove(pm);

            string text = String.Format("[Duel] {0} accepted the challenge from {1}.", pm.Name, challenger.Name);
            pm.SendMessage(MessageHue, text);
            challenger.SendMessage(MessageHue, text);

            StartMatch(challenger, pm, challenge.Rounds, challenge.Rules);
        }

        public static void Decline(PlayerMobile pm)
        {
            DuelChallenge challenge;

            if (!m_Pending.TryGetValue(pm, out challenge))
            {
                pm.SendMessage(MessageHue, "[Duel] No pending challenge.");
                return;
            }

            m_Pending.Remove(pm);

            string text = String.Format("[Duel] {0} declined the challenge from {1}.", pm.Name, challenge.Challenger.Name);
            pm.SendMessage(MessageHue, text);
            challenge.Challenger.SendMessage(MessageHue, text);
            Console.WriteLine(text);
        }

        public static bool CancelChallengeBy(PlayerMobile challenger)
        {
            bool any = false;

            foreach (var kv in m_Pending.Where(kv => kv.Value.Challenger == challenger).ToList())
            {
                m_Pending.Remove(kv.Key);
                kv.Key.SendMessage(MessageHue, String.Format("[Duel] {0} cancelled the challenge.", challenger.Name));
                any = true;
            }

            return any;
        }

        private static void ExpireChallenges()
        {
            foreach (var kv in m_Pending.Where(kv => kv.Value.Expired).ToList())
            {
                m_Pending.Remove(kv.Key);

                string text = String.Format("[Duel] The challenge from {0} to {1} has expired.", kv.Value.Challenger.Name, kv.Value.Target.Name);
                kv.Value.Challenger.SendMessage(MessageHue, text);
                kv.Value.Target.SendMessage(MessageHue, text);
            }
        }

        /// <summary>Starts a match immediately (used by [Accept and the staff [Duel start shortcut). Returns false with a message to 'issuer' on failure.</summary>
        public static bool StartMatch(PlayerMobile a, PlayerMobile b, int rounds, DuelRules rules, Mobile issuer = null)
        {
            if (a == b)
            {
                if (issuer != null) issuer.SendMessage(MessageHue, "[Duel] A fighter cannot duel themselves.");
                return false;
            }

            foreach (PlayerMobile pm in new[] { a, b })
            {
                string problem = CheckAvailable(pm, rules);

                if (problem != null)
                {
                    if (issuer != null) issuer.SendMessage(MessageHue, problem);
                    return false;
                }
            }

            rounds = Math.Max(1, Math.Min(MaxRounds, rounds));

            DuelArena arena = DuelArena.FindFree();

            if (arena == null)
            {
                if (issuer != null) issuer.SendMessage(MessageHue, "[Duel] All arenas are busy.");
                return false;
            }

            m_Pending.Remove(a);
            m_Pending.Remove(b);

            var match = new DuelMatch(arena, a, b, rounds, rules ?? DuelRules.Default);

            arena.Match = match;
            Matches.Add(match);
            match.Start();

            return true;
        }

        public static void OnMatchFinished(DuelMatch match)
        {
            Matches.Remove(match);

            if (match.Arena.Match == match)
                match.Arena.Match = null;
        }

        /// <summary>Staff reset: abort the match in one arena (or all), clear challenges, heal and unfreeze everyone standing there.</summary>
        public static void Reset(Mobile staff, DuelArena only)
        {
            foreach (DuelMatch match in Matches.ToList())
            {
                if (only == null || match.Arena == only)
                    match.Abort("reset by staff");
            }

            if (only == null)
                m_Pending.Clear();

            foreach (DuelArena arena in DuelArena.All)
            {
                if (only != null && arena != only)
                    continue;

                if (arena.Busy)
                    arena.Match = null;

                foreach (Mobile m in arena.Region.GetPlayers())
                {
                    m.Frozen = false;

                    if (!m.Alive)
                        m.Resurrect();

                    DuelMatch.FullHeal(m);
                }
            }

            staff.SendMessage(MessageHue, only == null ? "[Duel] Reset complete." : String.Format("[Duel] Arena {0} reset complete.", only.Id));
        }

        #endregion

        #region Stats

        public static DuelRecord GetRecord(Mobile m, bool create)
        {
            DuelRecord rec;

            if (!m_Stats.TryGetValue(m, out rec) && create)
                m_Stats[m] = rec = new DuelRecord();

            return rec;
        }

        public static KeyValuePair<Mobile, DuelRecord>? FindRecordByName(string name)
        {
            foreach (var kv in m_Stats)
            {
                if (kv.Key != null && !kv.Key.Deleted && Insensitive.Equals(kv.Key.Name, name))
                    return kv;
            }

            return null;
        }

        public static void RecordRound(Mobile winner, Mobile loser)
        {
            GetRecord(winner, true).RoundWins++;
            GetRecord(loser, true).RoundLosses++;
        }

        public static void RecordMatch(Mobile a, Mobile b, Mobile winner)
        {
            DuelRecord ra = GetRecord(a, true), rb = GetRecord(b, true);

            if (winner == null)
            {
                ra.MatchDraws++;
                rb.MatchDraws++;
            }
            else if (winner == a)
            {
                ra.MatchWins++;
                rb.MatchLosses++;
            }
            else
            {
                rb.MatchWins++;
                ra.MatchLosses++;
            }
        }

        #endregion

        #region Persistence

        private static void OnWorldSave(WorldSaveEventArgs e)
        {
            Persistence.Serialize(SavePath, writer =>
            {
                writer.Write(0); // version

                var entries = m_Stats.Where(kv => kv.Key != null && !kv.Key.Deleted).ToList();

                writer.Write(entries.Count);

                foreach (var kv in entries)
                {
                    writer.Write(kv.Key);
                    writer.Write(kv.Value.MatchWins);
                    writer.Write(kv.Value.MatchLosses);
                    writer.Write(kv.Value.MatchDraws);
                    writer.Write(kv.Value.RoundWins);
                    writer.Write(kv.Value.RoundLosses);
                }
            });
        }

        private static void OnWorldLoad()
        {
            Persistence.Deserialize(SavePath, reader =>
            {
                reader.ReadInt(); // version

                int count = reader.ReadInt();

                for (int i = 0; i < count; i++)
                {
                    Mobile m = reader.ReadMobile();
                    var rec = new DuelRecord
                    {
                        MatchWins = reader.ReadInt(),
                        MatchLosses = reader.ReadInt(),
                        MatchDraws = reader.ReadInt(),
                        RoundWins = reader.ReadInt(),
                        RoundLosses = reader.ReadInt()
                    };

                    if (m != null)
                        m_Stats[m] = rec;
                }
            });
        }

        #endregion
    }
}
