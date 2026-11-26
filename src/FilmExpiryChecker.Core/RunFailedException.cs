namespace FilmExpiryChecker;

/// <summary>
/// A check that couldn't finish, for a reason worth telling the user in plain words (its <see cref="Exception.Message"/>).
/// Raised instead of finishing with results that would be wrong, such as every film "not found" because JustWatch
/// was down.
/// </summary>
public sealed class RunFailedException(string reason, Exception? cause = null) : Exception(reason, cause);
