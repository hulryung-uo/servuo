using System;
using System.Linq;
using Server.Commands;
using Server.Mobiles;

namespace Server.Engines.Dueling
{
    /// <summary>
    /// Speech commands. Everything a fighter needs is AccessLevel.Player so bot-driven characters can use them:
    ///   [Challenge &lt;name&gt; [rounds] [rules]   [Accept   [Decline   [DuelStats [name]   [Duel status|help|cancel
    /// Staff:
    ///   [Duel start &lt;A&gt; &lt;B&gt; [rounds] [rules]   [DuelReset   [Duel arena build|go
    /// </summary>
    public static class DuelCommands
    {
        public static void Register()
        {
            CommandSystem.Register("Challenge", AccessLevel.Player, Challenge_OnCommand);
            CommandSystem.Register("Accept", AccessLevel.Player, Accept_OnCommand);
            CommandSystem.Register("Decline", AccessLevel.Player, Decline_OnCommand);
            CommandSystem.Register("DuelStats", AccessLevel.Player, DuelStats_OnCommand);
            CommandSystem.Register("Duel", AccessLevel.Player, Duel_OnCommand);
            CommandSystem.Register("DuelReset", AccessLevel.GameMaster, DuelReset_OnCommand);
        }

        public static void SendUsage(Mobile m)
        {
            m.SendMessage(DuelSystem.MessageHue, "[Duel] Commands: [Challenge <name> [rounds] [rules] | [Accept | [Decline | [DuelStats [name] | [Duel status | [Duel cancel");
            m.SendMessage(DuelSystem.MessageHue, "[Duel] Rules: " + DuelRules.ValidTokens + " (join with '-', e.g. 5x-katana). Default: best of 3, rules any.");

            if (m.AccessLevel >= AccessLevel.GameMaster)
                m.SendMessage(DuelSystem.MessageHue, "[Duel] Staff: [Duel start <A> <B> [rounds] [rules] | [DuelReset | [Duel arena build | [Duel arena go");
        }

        /// <summary>Parses "[rounds] [rules...]" starting at args[index]. Returns false after messaging the issuer on a bad token.</summary>
        private static bool ParseRoundsAndRules(CommandEventArgs e, int index, out int rounds, out DuelRules rules)
        {
            rounds = DuelSystem.DefaultRounds;
            rules = DuelRules.Default;

            if (e.Length <= index)
                return true;

            int parsed;

            if (Int32.TryParse(e.Arguments[index], out parsed))
            {
                if (parsed < 1 || parsed > DuelSystem.MaxRounds)
                {
                    e.Mobile.SendMessage(DuelSystem.MessageHue, String.Format("[Duel] Rounds must be between 1 and {0}.", DuelSystem.MaxRounds));
                    return false;
                }

                rounds = parsed;
                index++;
            }

            if (e.Length <= index)
                return true;

            string text = String.Join(" ", e.Arguments.Skip(index));
            string error;

            if (!DuelRules.TryParse(text, out rules, out error))
            {
                e.Mobile.SendMessage(DuelSystem.MessageHue, "[Duel] " + error);
                return false;
            }

            return true;
        }

        [Usage("Challenge <name> [rounds] [rules]")]
        [Description("Challenges another player to a best-of-N duel in the arena, e.g. [Challenge Rook 3 5x-katana. They answer with [Accept or [Decline.")]
        private static void Challenge_OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile as PlayerMobile;

            if (from == null)
                return;

            if (e.Length < 1)
            {
                SendUsage(from);
                return;
            }

            if (e.Arguments[0].Equals("cancel", StringComparison.OrdinalIgnoreCase))
            {
                from.SendMessage(DuelSystem.MessageHue, DuelSystem.CancelChallengeBy(from) ? "[Duel] Challenge cancelled." : "[Duel] You have no pending challenge.");
                return;
            }

            PlayerMobile target = DuelSystem.FindOnlinePlayer(e.Arguments[0]);

            if (target == null)
            {
                from.SendMessage(DuelSystem.MessageHue, String.Format("[Duel] {0} is not online.", e.Arguments[0]));
                return;
            }

            int rounds;
            DuelRules rules;

            if (!ParseRoundsAndRules(e, 1, out rounds, out rules))
                return;

            DuelSystem.Challenge(from, target, rounds, rules);
        }

        [Usage("Accept")]
        [Description("Accepts the pending duel challenge.")]
        private static void Accept_OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile as PlayerMobile;

            if (from != null)
                DuelSystem.Accept(from);
        }

        [Usage("Decline")]
        [Description("Declines the pending duel challenge.")]
        private static void Decline_OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile as PlayerMobile;

            if (from != null)
                DuelSystem.Decline(from);
        }

        [Usage("DuelStats [name]")]
        [Description("Prints duel win/loss statistics for yourself or the named player.")]
        private static void DuelStats_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;

            if (e.Length < 1)
            {
                DuelRecord own = DuelSystem.GetRecord(from, false) ?? new DuelRecord();
                from.SendMessage(DuelSystem.MessageHue, own.Describe(from.Name));
                return;
            }

            string name = e.Arguments[0];
            Mobile target = DuelSystem.FindOnlinePlayer(name);

            if (target != null)
            {
                DuelRecord rec = DuelSystem.GetRecord(target, false) ?? new DuelRecord();
                from.SendMessage(DuelSystem.MessageHue, rec.Describe(target.Name));
                return;
            }

            var found = DuelSystem.FindRecordByName(name);

            if (found.HasValue)
                from.SendMessage(DuelSystem.MessageHue, found.Value.Value.Describe(found.Value.Key.Name));
            else
                from.SendMessage(DuelSystem.MessageHue, String.Format("[Duel] No stats for {0}.", name));
        }

        [Usage("Duel status | help | cancel | start <A> <B> [rounds] [rules] | arena build|go")]
        [Description("Duel system utilities. 'start' and 'arena' are staff only.")]
        private static void Duel_OnCommand(CommandEventArgs e)
        {
            Mobile from = e.Mobile;
            string sub = e.Length > 0 ? e.Arguments[0].ToLowerInvariant() : "help";

            switch (sub)
            {
                case "status":
                    {
                        var match = DuelSystem.Current;
                        from.SendMessage(DuelSystem.MessageHue, match != null ? match.StatusLine() : "[Duel] Status: no match in progress.");
                        break;
                    }
                case "cancel":
                    {
                        var pm = from as PlayerMobile;
                        bool any = pm != null && DuelSystem.CancelChallengeBy(pm);
                        from.SendMessage(DuelSystem.MessageHue, any ? "[Duel] Challenge cancelled." : "[Duel] You have no pending challenge.");
                        break;
                    }
                case "start":
                    {
                        if (!RequireStaff(from))
                            return;

                        if (e.Length < 3)
                        {
                            from.SendMessage(DuelSystem.MessageHue, "[Duel] Usage: [Duel start <A> <B> [rounds] [rules]");
                            return;
                        }

                        PlayerMobile a = DuelSystem.FindOnlinePlayer(e.Arguments[1]);
                        PlayerMobile b = DuelSystem.FindOnlinePlayer(e.Arguments[2]);

                        if (a == null || b == null)
                        {
                            from.SendMessage(DuelSystem.MessageHue, String.Format("[Duel] {0} is not online.", a == null ? e.Arguments[1] : e.Arguments[2]));
                            return;
                        }

                        int rounds;
                        DuelRules rules;

                        if (!ParseRoundsAndRules(e, 3, out rounds, out rules))
                            return;

                        DuelSystem.StartMatch(a, b, rounds, rules, from);
                        break;
                    }
                case "stop":
                case "reset":
                    {
                        if (RequireStaff(from))
                            DuelSystem.Reset(from);
                        break;
                    }
                case "arena":
                    {
                        if (!RequireStaff(from))
                            return;

                        string action = e.Length > 1 ? e.Arguments[1].ToLowerInvariant() : "go";

                        if (action == "build" || action == "rebuild")
                        {
                            int count = DuelArena.Build();
                            from.SendMessage(DuelSystem.MessageHue, String.Format("[Duel] Arena rebuilt: {0} fence pieces, stone at {1}.", count, DuelArena.StoneLocation));
                        }
                        else
                        {
                            from.MoveToWorld(DuelArena.StoneLocation, DuelArena.ArenaMap);
                        }

                        break;
                    }
                default:
                    SendUsage(from);
                    break;
            }
        }

        [Usage("DuelReset")]
        [Description("Aborts the current duel, clears pending challenges and heals everyone in the arena.")]
        private static void DuelReset_OnCommand(CommandEventArgs e)
        {
            DuelSystem.Reset(e.Mobile);
        }

        private static bool RequireStaff(Mobile m)
        {
            if (m.AccessLevel >= AccessLevel.GameMaster)
                return true;

            m.SendMessage(DuelSystem.MessageHue, "[Duel] Staff only.");
            return false;
        }
    }
}
