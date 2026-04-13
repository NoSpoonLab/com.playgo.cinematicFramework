using UnityEngine;

public static class Enums 
{
    public enum Language
    {
        None, Spanish, English
    }
    public enum StoryEntryType
    {
        None,
        LINE,
        QUESTION
    }
    public enum StoryAnswerOption
    {
        None,
        A,
        B
    }
    public enum PlacementContext
    {
        Startup,
        Scene,
        Debug,
        Ending
    }

    public enum WrongAnswerFlowMode
    {
        [InspectorName("Go To Previous Entry")]
        GoToPreviousEntry,

        [InspectorName("Continue Forward")]
        ContinueForward
    }
}
