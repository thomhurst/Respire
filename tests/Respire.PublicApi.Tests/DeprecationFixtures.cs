namespace Respire.PublicApi.Tests;

// These exported APIs deliberately live outside the shipping core assembly.
[Obsolete("Missing migration metadata.")]
public class InvalidDeprecatedType;

[Obsolete("Use the replacement.", DiagnosticId = "TEST001", UrlFormat = "https://example.com/migrations/{0}")]
public class ValidDeprecatedType;

// Its implicit constructor has the compiler's Obsolete compatibility marker, not a deprecation.
public class RequiredMembersFixture
{
    public required int Value { get; init; }
}

public class DeprecatedRequiredMembersFixture
{
    public required int Value { get; init; }

    [Obsolete("Use the replacement constructor.", true)]
    public DeprecatedRequiredMembersFixture() { }
}

public class DeprecationFixtures
{
    [Obsolete("Missing migration metadata.")]
    public DeprecationFixtures() { }

    [Obsolete("Missing diagnostic.", UrlFormat = "https://example.com/migrations/{0}")]
    public void InvalidMethod() { }

    [Obsolete("Insecure URL.", DiagnosticId = "TEST001", UrlFormat = "http://example.com/{0}")]
    public int InvalidProperty => 0;

    [Obsolete("Missing placeholder.", DiagnosticId = "TEST001", UrlFormat = "https://example.com/migrations")]
    public int InvalidField;

    [Obsolete("Missing URL.", DiagnosticId = "TEST001")]
    public event Action InvalidEvent { add { } remove { } }

    [Obsolete("Use the replacement.", DiagnosticId = "TEST001", UrlFormat = "https://example.com/migrations/{0}")]
    public void ValidMethod() { }

    [Obsolete("Private APIs are outside the policy.")]
    private void PrivateMethod() { }
}

public class ProtectedDeprecationFixtures
{
    [Obsolete("Missing migration metadata.")]
    protected ProtectedDeprecationFixtures() { }

    [Obsolete("Missing migration metadata.")]
    protected void InvalidMethod() { }

    [Obsolete("Missing migration metadata.")]
    protected internal int InvalidProperty { get; private set; }

    [Obsolete("Missing migration metadata.")]
    protected internal static int InvalidField;

    [Obsolete("Missing migration metadata.")]
    protected event Action InvalidEvent { add { } remove { } }

    [Obsolete("Missing migration metadata.")]
    protected class InvalidNestedType;

    protected internal class NestedApi
    {
        [Obsolete("Missing migration metadata.")]
        public void InvalidMethod() { }
    }

    [Obsolete("Use the replacement.", DiagnosticId = "TEST001", UrlFormat = "https://example.com/migrations/{0}")]
    protected void ValidMethod() { }

    [Obsolete("Assembly-only APIs are outside the policy.")]
    private protected void PrivateProtectedMethod() { }

    [Obsolete("Assembly-only APIs are outside the policy.")]
    internal int InternalProperty => 0;

    private protected class HiddenNestedApi
    {
        [Obsolete("The containing type is not externally accessible.")]
        public void IgnoredMethod() { }
    }
}
