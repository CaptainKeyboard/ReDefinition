using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ReDefinition.Dashboard
{
    // What every page of a game in progress may show, in no vessel: the game's mode, its funds, science
    // and reputation, the time and the calendar, and time warp; saving and leaving to the main menu.
    internal static class GameTelemetry
    {
        // The scenes with time warp but no vessel of their own; in flight it is there while the
        // flight runs.
        private static bool WarpShown
        {
            get
            {
                if (TimeWarp.fetch == null) return false;
                GameScenes scene = HighLogic.LoadedScene;
                return scene == GameScenes.SPACECENTER || scene == GameScenes.TRACKSTATION || Telemetry.PauseState() == 0;
            }
        }

        // The scenes saved and left as KSP's own pause menu there does it.
        private static bool LeavesLikeKsc
        {
            get { return HighLogic.LoadedScene == GameScenes.SPACECENTER || HighLogic.LoadedScene == GameScenes.TRACKSTATION; }
        }

        // g.mode: KSP's name of the game's mode (CAREER, SCIENCE_SANDBOX, SANDBOX ...), null without a game.
        internal static object Mode()
        {
            return HighLogic.CurrentGame != null ? HighLogic.CurrentGame.Mode.ToString() : null;
        }

        // Null where the game's mode keeps none.
        internal static object Funds()
        {
            return HighLogic.CurrentGame != null && Funding.Instance != null ? (object)Funding.Instance.Funds : null;
        }

        // g.science: the science points to spend; null where the game's mode keeps none.
        internal static object Science()
        {
            return HighLogic.CurrentGame != null && ResearchAndDevelopment.Instance != null
                ? (object)(double)ResearchAndDevelopment.Instance.Science
                : null;
        }

        // g.reputation: the space program's reputation; null where the game's mode keeps none.
        internal static object Reputation()
        {
            return HighLogic.CurrentGame != null && global::Reputation.Instance != null
                ? (object)(double)global::Reputation.Instance.reputation
                : null;
        }

        // g.ut: the universal time in seconds, null without a game.
        internal static object Ut()
        {
            return HighLogic.CurrentGame != null && Planetarium.fetch != null ? (object)Planetarium.GetUniversalTime() : null;
        }

        // As KSP writes the date, in its calendar or a mod's.
        internal static object Date()
        {
            object ut = Ut();
            return ut != null ? KSPUtil.PrintDate((double)ut, true, true) : null;
        }

        // The calendar's day and year in seconds, for countdowns written as KSP writes them.
        internal static object Calendar()
        {
            IDateTimeFormatter calendar = KSPUtil.dateTimeFormatter;
            return calendar == null ? null : new Dictionary<string, object> { { "day", calendar.Day }, { "year", calendar.Year } };
        }

        // t.currentRate: the time warp's factor, where the scene shows warp.
        internal static object WarpRate()
        {
            return WarpShown ? (object)(double)TimeWarp.CurrentRate : null;
        }

        // t.currentRateIndex: the index of that rate in the warp mode's rates, where the scene shows warp.
        internal static object WarpRateIndex()
        {
            return WarpShown ? (object)TimeWarp.CurrentRateIndex : null;
        }

        // t.timeWarp[index]: one of the rates of the warp mode in force, clamped to them.
        internal static void SetWarp(string[] args)
        {
            if (!WarpShown || args.Length == 0) return;
            double index;
            if (!double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out index)) return;
            float[] rates = TimeWarp.WarpMode == TimeWarp.Modes.LOW ? TimeWarp.fetch.physicsWarpRates : TimeWarp.fetch.warpRates;
            TimeWarp.SetRate(Mathf.Clamp((int)index, 0, rates.Length - 1), false);
        }

        // g.quicksave: as F5 saves at the Space Center and in the Tracking Station.
        internal static void QuickSave()
        {
            if (!LeavesLikeKsc || HighLogic.CurrentGame == null) return;
            GamePersistence.SaveGame("quicksave", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            ScreenMessages.PostScreenMessage("Quicksaving...", 2f, ScreenMessageStyle.UPPER_CENTER);
        }

        // g.mainMenu: saved and left as the Space Center's pause menu does it.
        internal static void MainMenu()
        {
            if (!LeavesLikeKsc || HighLogic.CurrentGame == null) return;
            Debug.Log(Core.Log.Tag + " Dashboard: saving and leaving to the main menu");
            GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            HighLogic.LoadScene(GameScenes.MAINMENU);
        }
    }
}
