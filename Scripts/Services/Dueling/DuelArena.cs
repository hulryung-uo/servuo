using System;
using System.Collections.Generic;
using System.Linq;
using Server.Items;
using Server.Mobiles;
using Server.Regions;
using Server.Spells;

namespace Server.Engines.Dueling
{
    /// <summary>
    /// Fixed arena on the z=20 plateau SW of the Minoc ridge (Felucca).
    /// Floor x 2598-2606, y 489-493; fence ring around it; lobby/spectator strip to the south (y 495-496).
    /// </summary>
    public static class DuelArena
    {
        public static Map ArenaMap { get { return Map.Felucca; } }

        public const int ArenaZ = 20;

        /// <summary>Region bounds: fence ring included (x 2597-2607, y 488-494).</summary>
        public static readonly Rectangle2D Bounds = new Rectangle2D(2597, 488, 11, 7);

        /// <summary>Walkable floor inside the fence (x 2598-2606, y 489-493).</summary>
        public static readonly Rectangle2D Floor = new Rectangle2D(2598, 489, 9, 5);

        public static readonly Point3D MarkA = new Point3D(2599, 491, ArenaZ);
        public static readonly Point3D MarkB = new Point3D(2605, 491, ArenaZ);

        /// <summary>Where fighters are placed when a match ends (lobby strip south of the fence).</summary>
        public static readonly Point3D ExitA = new Point3D(2599, 496, ArenaZ);
        public static readonly Point3D ExitB = new Point3D(2605, 496, ArenaZ);

        public static readonly Point3D StoneLocation = new Point3D(2602, 496, ArenaZ);

        public static readonly Point3D Center = new Point3D(2602, 491, ArenaZ);

        // Short iron fence set: 0x0849 runs along world Y (west/east edges), 0x084B along world X (north/south edges), 0x084A is the post.
        private const int FenceAlongY = 0x0849;
        private const int FencePost = 0x084A;
        private const int FenceAlongX = 0x084B;

        public static bool Contains(Mobile m)
        {
            return m != null && m.Map == ArenaMap && Bounds.Contains(m.Location);
        }

        public static bool IsBuilt()
        {
            return World.Items.Values.Any(i => i is DuelArenaFence && !i.Deleted);
        }

        /// <summary>Creates the fence and stone if they don't exist yet. Idempotent.</summary>
        public static int EnsureBuilt()
        {
            if (IsBuilt())
                return 0;

            return Build();
        }

        public static void Clear()
        {
            foreach (Item item in World.Items.Values.Where(i => i is DuelArenaFence || i is DuelStone).ToList())
                item.Delete();
        }

        /// <summary>Places fence pieces on every ring tile that sits on the z=20 plateau (cliff tiles need none) plus the duel stone.</summary>
        public static int Build()
        {
            Clear();

            Map map = ArenaMap;
            int count = 0;

            int x0 = Bounds.Start.X, y0 = Bounds.Start.Y;
            int x1 = Bounds.End.X - 1, y1 = Bounds.End.Y - 1;

            for (int x = x0; x <= x1; x++)
            {
                for (int y = y0; y <= y1; y++)
                {
                    bool onX = (y == y0 || y == y1);
                    bool onY = (x == x0 || x == x1);

                    if (!onX && !onY)
                        continue;

                    if (map.Tiles.GetLandTile(x, y).Z != ArenaZ)
                        continue; // cliff face above/below the plateau already blocks this tile

                    int itemId = (onX && onY) ? FencePost : (onX ? FenceAlongX : FenceAlongY);

                    var fence = new DuelArenaFence(itemId);
                    fence.MoveToWorld(new Point3D(x, y, ArenaZ), map);
                    count++;
                }
            }

            var stone = new DuelStone();
            stone.MoveToWorld(StoneLocation, map);

            return count;
        }

        /// <summary>Moves every non-staff player standing in the arena (except the given fighters) out to the lobby.</summary>
        public static void EvictOthers(Mobile keepA, Mobile keepB)
        {
            if (DuelSystem.Region == null)
                return;

            foreach (Mobile m in DuelSystem.Region.GetPlayers())
            {
                if (m == keepA || m == keepB || m.IsStaff())
                    continue;

                m.MoveToWorld(StoneLocation, ArenaMap);
                m.SendMessage(DuelSystem.MessageHue, "[Duel] You have been moved out of the arena.");
            }
        }
    }

    public class DuelRegion : BaseRegion
    {
        public const int RegionPriority = 60; // above TownRegion (50) so Minoc's guard zone never applies here

        public DuelRegion()
            : base("Duel Arena", DuelArena.ArenaMap, RegionPriority, DuelArena.Bounds)
        {
        }

        private static DuelMatch Match { get { return DuelSystem.Current; } }

        private static bool RoundLive
        {
            get { return Match != null && (Match.Phase == DuelPhase.Countdown || Match.Phase == DuelPhase.Fighting); }
        }

        public override bool AllowHousing(Mobile from, Point3D p)
        {
            return false;
        }

        public override bool OnBeginSpellCast(Mobile m, ISpell s)
        {
            if (m.IsStaff())
                return base.OnBeginSpellCast(m, s);

            m.SendMessage(DuelSystem.MessageHue, "[Duel] Spellcasting is not allowed in the arena.");
            return false;
        }

        public override bool AllowHarmful(Mobile from, IDamageable target)
        {
            if (from == null || from.IsStaff())
                return true;

            var match = Match;

            if (match == null || match.Phase != DuelPhase.Fighting)
                return false;

            var targetMobile = target as Mobile;

            return targetMobile != null && from != targetMobile && match.IsFighter(from) && match.IsFighter(targetMobile);
        }

        public override bool AllowBeneficial(Mobile from, Mobile target)
        {
            if (from == target || from.IsStaff())
                return true;

            var match = Match;

            if (match != null && match.Phase != DuelPhase.Finished && match.IsFighter(target))
                return false; // nobody may assist a duelist during a match

            return base.AllowBeneficial(from, target);
        }

        public override bool OnMoveInto(Mobile m, Direction d, Point3D newLocation, Point3D oldLocation)
        {
            if (RoundLive && !m.IsStaff() && !Match.IsFighter(m))
            {
                m.SendMessage(DuelSystem.MessageHue, "[Duel] A duel is in progress; you cannot enter the arena.");
                return false;
            }

            return base.OnMoveInto(m, d, newLocation, oldLocation);
        }

        public override bool OnDoubleClick(Mobile m, object o)
        {
            if (o is Corpse && ((Corpse)o).Owner != m && !m.IsStaff())
            {
                m.SendMessage(DuelSystem.MessageHue, "[Duel] You cannot loot corpses in the arena.");
                return false;
            }

            if (o is Bandage && !DuelSystem.AllowBandage(m, m))
                return false;

            return base.OnDoubleClick(m, o);
        }

        public override void OnDidHarmful(Mobile harmer, IDamageable harmed)
        {
            base.OnDidHarmful(harmer, harmed);

            var match = Match;

            if (match != null)
                match.NoteHarmful(harmer);
        }

        public override void OnDeath(Mobile m)
        {
            base.OnDeath(m);

            var match = Match;

            if (match != null)
                match.HandleDeath(m);
        }

        public override bool CheckTravel(Mobile traveller, Point3D p, TravelCheckType type)
        {
            // Nobody may recall/gate INTO the arena while a round is live; leaving is always allowed.
            if (RoundLive && !traveller.IsStaff() && !Match.IsFighter(traveller) && (type == TravelCheckType.RecallTo || type == TravelCheckType.GateTo))
                return false;

            return base.CheckTravel(traveller, p, type);
        }
    }

    public class DuelArenaFence : Item
    {
        [Constructable]
        public DuelArenaFence(int itemId)
            : base(itemId)
        {
            Movable = false;
            Name = "arena fence";
        }

        public DuelArenaFence(Serial serial)
            : base(serial)
        {
        }

        public override void Serialize(GenericWriter writer)
        {
            base.Serialize(writer);
            writer.Write(0); // version
        }

        public override void Deserialize(GenericReader reader)
        {
            base.Deserialize(reader);
            reader.ReadInt();
        }
    }

    public class DuelStone : Item
    {
        [Constructable]
        public DuelStone()
            : base(0xEDD)
        {
            Movable = false;
            Hue = 0x4E9;
            Name = "duel stone";
        }

        public DuelStone(Serial serial)
            : base(serial)
        {
        }

        public override void OnDoubleClick(Mobile from)
        {
            if (!from.InRange(GetWorldLocation(), 8))
            {
                from.SendMessage(DuelSystem.MessageHue, "[Duel] You are too far away from the duel stone.");
                return;
            }

            DuelCommands.SendUsage(from);

            var match = DuelSystem.Current;

            if (match != null)
                from.SendMessage(DuelSystem.MessageHue, match.StatusLine());
        }

        public override void Serialize(GenericWriter writer)
        {
            base.Serialize(writer);
            writer.Write(0); // version
        }

        public override void Deserialize(GenericReader reader)
        {
            base.Deserialize(reader);
            reader.ReadInt();
        }
    }
}
