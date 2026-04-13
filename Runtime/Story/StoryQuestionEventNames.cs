public static class StoryQuestionEventNames
{
    private const string QuestionShownSuffix = "_QuestionShown";
    private const string OptionASelectedSuffix = "_Option_A_Selected";
    private const string OptionBSelectedSuffix = "_Option_B_Selected";
    private const string FeedbackOkSuffix = "_Feedback_OK";
    private const string FeedbackKoSuffix = "_Feedback_KO";

    public static string ForQuestionShown(StoryEntry entry)
    {
        return Build(entry, QuestionShownSuffix);
    }

    public static string ForOptionSelected(StoryEntry entry, Enums.StoryAnswerOption option)
    {
        return option == Enums.StoryAnswerOption.B
            ? Build(entry, OptionBSelectedSuffix)
            : Build(entry, OptionASelectedSuffix);
    }

    public static string ForFeedback(StoryEntry entry, bool isCorrect)
    {
        return Build(entry, isCorrect ? FeedbackOkSuffix : FeedbackKoSuffix);
    }

    private static string Build(StoryEntry entry, string suffix)
    {
        if (entry == null || string.IsNullOrEmpty(entry.id))
            return string.Empty;

        return string.Concat(entry.id, suffix);
    }

    /// <summary>
    /// True si el nombre corresponde a uno de los eventos semánticos de QUESTION para este entry.
    /// Útil para distinguir eventos de graph frente a IDs de VO.
    /// </summary>
    public static bool IsSemanticEventForEntry(string eventName, StoryEntry entry)
    {
        if (string.IsNullOrEmpty(eventName) || entry == null || string.IsNullOrEmpty(entry.id))
            return false;

        int idLen = entry.id.Length;
        if (eventName.Length <= idLen + 1)
            return false;

        if (string.Compare(eventName, 0, entry.id, 0, idLen, System.StringComparison.OrdinalIgnoreCase) != 0)
            return false;

        if (eventName[idLen] != '_')
            return false;

        return eventName.EndsWith(QuestionShownSuffix, System.StringComparison.OrdinalIgnoreCase)
               || eventName.EndsWith(OptionASelectedSuffix, System.StringComparison.OrdinalIgnoreCase)
               || eventName.EndsWith(OptionBSelectedSuffix, System.StringComparison.OrdinalIgnoreCase)
               || eventName.EndsWith(FeedbackOkSuffix, System.StringComparison.OrdinalIgnoreCase)
               || eventName.EndsWith(FeedbackKoSuffix, System.StringComparison.OrdinalIgnoreCase);
    }
}
