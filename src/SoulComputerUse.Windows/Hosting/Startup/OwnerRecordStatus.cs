namespace SoulComputerUse.Windows.Hosting.Startup;

/// <summary>
/// What this account's ownership record means for a new start. It keeps the recovery rules of
/// section 3 in one place: a record that parses and meets the takeover criteria may be replaced, and
/// everything else that is not an absent record must be inspected by hand.
/// </summary>
public enum OwnerRecordStatus
{
    /// <summary>No record exists for this account, which the contract treats as a normal start.</summary>
    Absent = 0,

    /// <summary>The owner is gone and the record meets the takeover criteria, so it may be replaced.</summary>
    TakeOver = 1,

    /// <summary>
    /// A record exists but cannot be parsed, has unusable fields, or fails the takeover criteria. It is
    /// never deleted automatically, because it is the only evidence of what the previous owner left.
    /// </summary>
    NotTakeable = 2,
}
