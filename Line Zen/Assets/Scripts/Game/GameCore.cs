﻿using System;
using System.Collections.Generic;
using UnityEngine;

public class GameCore : MonoBehaviour
{
    // Setup and linsk
    [Header("Base links")]
    public Data data;
    public Events events;
    public GameInputManager inputManager;

    // Static values
    public static readonly float widthLeeway = 0.025f;
    public static readonly float bubbleRadiusStandard = .3f;
    public static readonly float bubbleRadiusLarge = bubbleRadiusStandard * 2;
    public static readonly int bonusThreshold = 1;
    public static readonly int pointsPerBubble = 20;
    public static readonly int pointsPerBonusBubble = 10;
    public static readonly int maxBubblesOnScreen = 30;
    public static readonly string compressionIndicator = "c_";

    [Header("Levels")]
    public TextAsset tutorialOne;
    public TextAsset tutorialTwo;
    public TextAsset internalLevel;
    // There's an implicit "star1" which is any level of completion.
    private int star2 = 0;
    private int star3 = 0;

    [Header("Internals")]
    public GameMode internalMode;
    public GameState internalState;

    [Header("Levels")]
    public SOChallengeList levels;

    // Private internal state
    private bool wasDownPreviously;
    private DataPoint lastLineStart;
    private DataPoint lastLineEnd;
    private bool hasInit;
    private bool isPaused;

    // Internal bubble and level tracking
    private List<DataBubble> bubbles;
    private List<Tuple<DataPoint, DataPoint>> guideLines;
    private int linesDrawn;

    // Unlimited info
    private Utils.WichmannRng rand;

    bool internalStateCurrentHasInit;
    public GameState State
    {
        get
        {
            return internalState;
        }
        set
        {
            internalState = value;
            internalStateCurrentHasInit = false;
            if (internalState != GameState.Game)
            {
                events.OnRequestPauseState?.Invoke(false);
            }

            events.OnGameStateChange?.Invoke(internalState, Mode);
        }
    }

    private GameMode Mode
    {
        get
        {
            return internalMode;
        }
        set
        {
            internalMode = value;
        }
    }

    public TextAsset CurrentLevel
    {
        get
        {
            return internalLevel;
        }
        set
        {
            internalLevel = value;
            linesDrawn = 0;
            star2 = 0;
            star3 = 0;
        }
    }

    public SOChallengeList ChallengeLevels
    {
        get
        {
            return levels;
        }
    }

    public int NumberOfBubbles
    {
        get
        {
            return bubbles == null ? 0 : bubbles.Count;
        }
    }

    private void Start()
    {
        hasInit = false;
        linesDrawn = 0;
        internalStateCurrentHasInit = false;
        bubbles = new List<DataBubble>();
        guideLines = new List<Tuple<DataPoint, DataPoint>>();
        wasDownPreviously = false;

        events.OnShowHelpToggle += (isOn) => {
            DataGeneral toModify = data.GetDataGeneral();
            toModify.showHelp = isOn;
            data.SetDataGeneral(toModify);
        };

        events.OnShowParticlesToggle += (isOn) => {
            DataGeneral toModify = data.GetDataGeneral();
            toModify.showParticles = isOn;
            data.SetDataGeneral(toModify);
        };

        events.OnTutorialToggle += (isOn) => {
            DataGeneral toModify = data.GetDataGeneral();
            toModify.showTutorial = isOn;
            data.SetDataGeneral(toModify);
        };

        events.OnNoSaveEntryFound += (name) => {
            DataGeneral toModify = data.GetDataGeneral();
            toModify.challenges.Add(new DataChallenge(0, name));
            data.SetDataGeneral(toModify);
        };

        events.OnLevelLoadRequest += (levelName) => {
            Mode = GameMode.ChallengeLevel;
            CurrentLevel = levels.GetByName(levelName);
            PopulateLevelBubbles(CurrentLevel);
            State = GameState.Game;
        };

        events.OnLevelReloadRequest += () => {
            Mode = GameMode.ChallengeLevel;
            CurrentLevel = CurrentLevel; // Done to force the value resets. Not ideal technique for this.
            PopulateLevelBubbles(CurrentLevel);
            State = GameState.Game;
        };

        events.OnGameStateChangeRequest += (state, mode) => {
            Mode = mode;
            State = state;
        };

        events.OnRequestPauseState += (state) => {
            isPaused = state;
            events.OnEnactPauseState?.Invoke(isPaused);
        };

        // Note that by default, we should make sure we don't have a visible pause menu.
        // Go through standard request line.
        events.OnRequestPauseState?.Invoke(false);
    }

    private void Update()
    {
        if (!hasInit)
        {
            data.Initialize();
            events.OnGameStateChange?.Invoke(internalState, Mode);
            events.OnGameInitialized?.Invoke();
            hasInit = true;
        }

        switch (State)
        {
            case GameState.Startup:
                break;
            case GameState.Options:
                break;
            case GameState.PickMode:
                break;
            case GameState.TutorialOne:
                if (!data.GetDataGeneral().showTutorial)
                {
                    State = GameState.PickMode;
                    PopulateLevelBubbles(levelData: null);
                    goto case GameState.PickMode;
                }
                else
                {
                    PopulateLevelBubbles(tutorialOne);
                    UpdatePlayerLine(recordScore: false);
                    CheckIfDoneTutorialBubbles(GameState.TutorialTwo);
                    break;
                }
            case GameState.TutorialTwo:
                PopulateLevelBubbles(tutorialTwo);
                UpdatePlayerLine(recordScore: false);
                CheckIfDoneTutorialBubbles(GameState.PickMode);
                ResetTutorialIfIncomplete();
                break;
            case GameState.PickChallenge:
                break;
            case GameState.Game:
                if (Mode == GameMode.ChallengeLevel)
                {
                    PopulateLevelBubbles(CurrentLevel);
                    UpdatePlayerLine(recordScore: false);
                    CheckIfDoneChallengeBubbles(GameState.Game);
                }
                else if (Mode == GameMode.Infinite)
                {
                    PopulateUnlimitedBubbles();
                    UpdatePlayerLine(recordScore: true);
                    CheckIfDoneUnlimitedBubbles();
                }
                break;
            case GameState.Exit:
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#else
                Application.Quit();
#endif 
                break;
            default:
                break;
        }
    }

    public void ResetTutorialIfIncomplete()
    {
        if (bubbles.Count == 1)
        {
            internalStateCurrentHasInit = false;
        }
    }

    // Parses the files and places bubbles in the world based on type, position, etc.
    public void PopulateLevelBubbles(TextAsset levelData)
    {
        if (!internalStateCurrentHasInit)
        {
            CurrentLevel = levelData;

            bubbles.Clear();
            guideLines.Clear();

            if (levelData != null)
            {

                string content = levelData.text;
                string[] lines = content.Split('\n');

                foreach (string line in lines)
                {
                    // Split on the line, and skip if it's an empty line
                    string[] split = line.Split('=');
                    if (split.Length == 1)
                    {
                        continue;
                    }

                    // Establish key/value for parsing, keys are case insensitve but really 
                    // should be lowercase.
                    string key = split[0].Trim().ToLowerInvariant();
                    string value = split[1].Trim();

                    // Bubbles can appear as a duplicate key, and are treated as such.
                    if (key.Contains("bubble"))
                    {
                        BubbleType bubbleType = BubbleType.Standard;
                        if (key.Contains("large"))
                        {
                            bubbleType = BubbleType.Large;
                        }

                        // There are two formats for the bubble value in the serialized file.
                        // The first of the two is just the location of the bubble in the world itself,
                        // and the second specifies any movement associated with the bubble.
                        //
                        // .75,1:0,-1@.8
                        //  ^ Initial X position
                        //     ^ Initial Y position
                        //      ^ split to see if we have a velocity
                        //       ^ Initail X velocity
                        //          ^ Initial Y velocity
                        //              ^ Initial Speed (sinusoidal)
                        //
                        string[] movementSplit = value.Split(':'); // Check to see if we have velocity info. 
                        float target_x = 0;
                        float target_y = 0;
                        float speed = 0;

                        // If we found the additional informaiton section, we can parse out velocity and speed.
                        // Once we start parsing this section, we assume it's formatted correctly.
                        if (movementSplit.Length > 1)
                        {
                            string[] speedSplit = movementSplit[1].Split('@');
                            string[] velocity = speedSplit[0].Split(',');
                            target_x = float.Parse(velocity[0].Trim());
                            target_y = float.Parse(velocity[1].Trim());
                            speed = float.Parse(speedSplit[1].Trim());
                        }

                        // We require the position at the very least. It's impoertant 
                        string[] point = movementSplit[0].Split(',');
                        float x = float.Parse(point[0].Trim());
                        float y = float.Parse(point[1].Trim());

                        // Add a new bubble with parsed info 
                        bubbles.Add(new DataBubble(new DataPoint(x, y), new DataPoint(target_x, target_y), speed, bubbleType));
                    }

                    // Lines can be duplicate keys, and so are treated as such
                    else if (key.Equals("line"))
                    {
                        string[] multiplePoints = value.Split(':');
                        string[] point1 = multiplePoints[0].Split(',');
                        string[] point2 = multiplePoints[1].Split(',');

                        float x1 = float.Parse(point1[0].Trim());
                        float y1 = float.Parse(point1[1].Trim());
                        float x2 = float.Parse(point2[0].Trim());
                        float y2 = float.Parse(point2[1].Trim());

                        guideLines.Add(new Tuple<DataPoint, DataPoint>(
                                new DataPoint(x1, y1),
                                new DataPoint(x2, y2)));
                    }

                    // There can only be one star entry, and so this entry will write over with the 
                    // last entry in the file if multiple are present.
                    else if (key.Equals("stars"))
                    {
                        string[] values = value.Split(',');

                        star2 = int.Parse(values[0].Trim());
                        star3 = int.Parse(values[1].Trim());
                    }
                }

                internalStateCurrentHasInit = true;
            }

            events.OnBubblesChange?.Invoke(bubbles);
            events.OnGuideLinesChange?.Invoke(guideLines);
        }
    }

    // Check to see if bubbles have been collected
    private void CheckIfDoneChallengeBubbles(GameState nextState)
    {
        if (CurrentLevel == null || levels == null)
        {
            State = GameState.Game;
            return;
        }

        if (bubbles.Count < 1)
        {
            int target = levels.levels.IndexOf(CurrentLevel) + 1;

            if (linesDrawn > 0)
            {
                DataGeneral toModify = data.GetDataGeneral();

                if (linesDrawn <= star3)
                {
                    toModify.SetChallengeStats(internalLevel.name, 3);
                }
                else if (linesDrawn <= star2)
                {
                    toModify.SetChallengeStats(internalLevel.name, 2);
                }
                else
                {
                    toModify.SetChallengeStats(internalLevel.name, 1);
                }

                data.SetDataGeneral(toModify);
            }

            if (target >= levels.levels.Count)
            {
                State = GameState.PickChallenge;
                CurrentLevel = null;
                return;
            }
            else
            {
                CurrentLevel = levels.levels[target];
                State = nextState;
                return;
            }
        }
    }

    // A tutorial may only continue 
    private void CheckIfDoneTutorialBubbles(GameState nextState)
    {
        if (CurrentLevel == null)
        {
            State = GameState.Game;
            return;
        }

        if (bubbles.Count < 1)
        {
            State = nextState;
            PopulateLevelBubbles(null);
            return;
        }
    }

    // Unlimited bubbles are done if we have no bubbles left
    private void CheckIfDoneUnlimitedBubbles()
    {
        if (bubbles.Count < 1)
        {
            DataGeneral gen = data.GetDataGeneral();
            gen.level = gen.level + 1;
            data.SetDataGeneral(gen);

            State = GameState.Game;
            return;
        }
    }

    // Unlimited bubbles are positioned in a random orientation
    private void PopulateUnlimitedBubbles()
    {
        if (!internalStateCurrentHasInit)
        {
            DataGeneral readOnlyData = data.GetDataGeneral();
            int seed = readOnlyData.seed;
            int level = readOnlyData.level;

            rand = new Utils.WichmannRng(seed + level);
            bubbles.Clear();
            guideLines.Clear();

            float bubbleCountFloat = 12.0f * Mathf.Log10(((float)level) + 9.1f) - 9.9f;

            while (bubbles.Count < bubbleCountFloat)
            {
                double x = (rand.Next() - 0.5f) * 2;
                double y = (rand.Next() - 0.5f) * 2;
                DataPoint screenSize = inputManager.ScreenSizeWorld();
                DataPoint pos = new DataPoint(x * (screenSize.X - bubbleRadiusStandard), y * (screenSize.Y - bubbleRadiusStandard * 2));
                bubbles.Add(new DataBubble(new DataPoint(pos)));
            }

            events.OnBubblesChange?.Invoke(bubbles);
            events.OnGuideLinesChange?.Invoke(guideLines);

            internalStateCurrentHasInit = true;
        }
    }

    // Handles updating positions for the player line, along with line starting and finishing events.
    private void UpdatePlayerLine(bool recordScore)
    {
        if (isPaused)
        {
            return;
        }

        if (inputManager.PrimaryInputDown())
        {
            if (wasDownPreviously)
            {
                lastLineEnd = inputManager.PrimaryInputPosWorld();
                events.OnLineUpdated?.Invoke(lastLineStart, lastLineEnd);
            }
            else
            {
                lastLineStart = inputManager.PrimaryInputPosWorld();
                lastLineEnd = inputManager.PrimaryInputPosWorld();
                events.OnLineCreated?.Invoke(lastLineStart, lastLineEnd);
            }

            wasDownPreviously = true;
        }
        else
        {
            if (wasDownPreviously)
            {
                DataEarnedScore points = CollectBubblesAsNecessary(recordScore);
                events.OnBubblesChange?.Invoke(bubbles);
                events.OnLineDestroyed?.Invoke(lastLineStart, lastLineEnd, points);
                wasDownPreviously = false;
            }
        }
    }

    // Returns how much the score changed by
    private DataEarnedScore CollectBubblesAsNecessary(bool impactsScore = true)
    {
        List<DataPoint> locs = new List<DataPoint>();
        List<int> collectedIndexes = new List<int>();

        // Collect collisions
        for (int i = bubbles.Count - 1; i >= 0; --i)
        {
            DataBubble bubble = bubbles[i];

            // Determine if we're hitting
            float triggerRadius = bubble.AdjustedRadius();
            bool isHit = Utils.IsLineTouchingCircle(lastLineStart, lastLineEnd, bubble.GetPosition(), triggerRadius, bubbleRadiusStandard);

            if (isHit)
            {
                collectedIndexes.Add(i);
                locs.Add(bubble.GetPosition());
            }
        }

        // Score updating
        int hit = collectedIndexes.Count;
        int scoreBase = GameCore.pointsPerBubble * hit;
        int scoreBonus = 0;

        if (hit > 0)
        {
            ++linesDrawn;
        }

        if (hit > bonusThreshold)
        {
            int bonusHits = hit - bonusThreshold;
            scoreBonus = bonusHits * bonusHits * pointsPerBonusBubble;
        }

        DataEarnedScore dataEarnedScore = new DataEarnedScore(scoreBase, scoreBonus, locs);

        if (pointsPerBubble != 0)
        {
            DataGeneral gen = data.GetDataGeneral();
            gen.score = gen.score + dataEarnedScore.total;

            if (impactsScore)
            {
                data.SetDataGeneral(gen);
            }
        }

        // Clear colleted bubbles. The indexes are from back to front, so the removal is safe. 
        foreach (int index in collectedIndexes)
        {
            DataBubble bubble = bubbles[index];
            events.OnBubbleDestroyed?.Invoke(bubble.GetPosition());
            bubbles.RemoveAt(index);

            DataPoint[] newBubbles = DetermineSplits(bubble, lastLineStart, lastLineEnd);
            if(newBubbles != null)
            {
                foreach(DataPoint point in newBubbles)
                {
                    DataBubble newBubble = new DataBubble(point, new DataPoint(0, 0), speed: 0, BubbleType.Standard);
                    bubbles.Add(newBubble);
                }
            }
        }

        return dataEarnedScore;
    }

    // Calculates perpendicular bubbles
    public static DataPoint[] DetermineSplits(DataBubble bubble, DataPoint lineStart, DataPoint lineEnd)
    {
        BubbleType type = bubble.TypeOfBubble();

        if (type == BubbleType.Large)
        {
            float yDiff = lineEnd.Y - lineStart.Y;
            float xDiff = lineEnd.X - lineStart.X;

            // Normally, a slope is y/x. Perpendicular is -x/y.
            float mPerp = -(xDiff / yDiff);

            // We need to figure out the angle to calculate the X offset required for getting us to this radius via the function y = mPerp(X).
            //                      v Angle C
            //                     /|
            //                    / |
            //       side  b ->  /  |  <- Side a
            //      (radius)    /   |
            //                 /    |
            //                /    _|
            //    angle A -> .____|_| <- Angle B (90 deg)
            //                  ^ side c
            float B = Mathf.Deg2Rad * 90f;
            float A = Mathf.Atan(mPerp);
            float C = (Mathf.Deg2Rad * 180) - A - B;

            float b = bubble.RawRadius();
            float a = b * Mathf.Sin(A) / Mathf.Sin(B); // This is the Y offset
            float c = b * Mathf.Sin(C) / Mathf.Sin(B);  // This is the X offset

            DataPoint start = bubble.GetPosition();
            DataPoint b1 = new DataPoint(start.X + c, start.Y + a);
            DataPoint b2 = new DataPoint(start.X - c, start.Y - a);

            return new DataPoint[] {b1, b2};
        }

        return null;
    }
}
