namespace Masar.Application.Common;

// AuthResult, LocationResult, and WorkspaceResult are three independent,
// hand-written copies of this exact same four-property shape
// (Succeeded / ErrorCode / ErrorMessage / Response). WorkspaceResult's own
// comment already flagged this as worth revisiting once a fourth case
// showed up — Amenity management is that fourth case, so this is that
// revisit, not a new decision made in isolation.
//
// The three existing Result classes are deliberately left untouched here.
// They're already shipped, reviewed, and working — retrofitting them to
// use this generic type is a pure refactor with no behavior change and no
// urgency, so it's a separate, optional cleanup, not bundled into this
// feature. Everything new from Step 9 onward uses this instead of adding
// a fifth copy.
public class Result<T>
{
    public bool Succeeded { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public T? Response { get; init; }

    public static Result<T> Success(T response) =>
        new() { Succeeded = true, Response = response };

    public static Result<T> Failure(string errorCode, string errorMessage) =>
        new() { Succeeded = false, ErrorCode = errorCode, ErrorMessage = errorMessage };
}
