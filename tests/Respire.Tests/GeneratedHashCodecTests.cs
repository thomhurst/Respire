using System.Globalization;
using TUnit.Assertions;
using TUnit.Core;

namespace Respire.Tests;

[RespireHash("user:{{{Id}}}:{Id}")]
internal partial record HashCodecUser(string Id, string Name, string? Session, int? Age, bool Active,
    long Count, double Score, decimal Balance, Guid Token, DateTimeOffset Created);

[RespireHash("mutable:{Id}")]
internal partial class HashCodecMutable
{
    public required string Id { get; init; }
    public required int Value { get; set; }
}

[RespireHash("number:{Id}:{Price}")]
internal partial record HashCodecNullable(int Id, decimal Price, bool? Flag, long? Count, double? Score,
    decimal? Amount, Guid? Token, DateTimeOffset? Created);

[RespireHash("{Id}")]
internal partial class HashCodecConstructor
{
    private int _id;
    private int _writes;
    public int Id { get => _id; set { _id = value; _writes++; } }
    public HashCodecConstructor(int id) { Id = id; }
    public int CountWrites() => _writes;
}

public class GeneratedHashCodecTests
{
    private static HashCodecUser Create(string? session = null, int? age = null) => new(
        "ü:{}", "姓名", session, age, true, long.MaxValue, 1.125, 1234.50M,
        Guid.Parse("754a1f42-3f49-42e9-9cb6-7e477b3e4098"),
        new DateTimeOffset(2026, 10, 8, 15, 0, 0, TimeSpan.FromHours(5.5)));

    [Test]
    [Arguments(null, null)]
    [Arguments("", 0)]
    [Arguments("非空", 42)]
    public async Task NullableEmptyAndUnicodeValuesRoundTrip(string? session, int? age)
    {
        var model = Create(session, age);
        var fields = HashCodecUserHashMapper.ToFields(model);
        await Assert.That(HashCodecUserHashMapper.FromFields(fields)).IsEqualTo(model);
        await Assert.That(fields.ContainsKey("Session")).IsEqualTo(session is not null);
        await Assert.That(fields.ContainsKey("Age")).IsEqualTo(age is not null);
        await Assert.That(fields["Active"]).IsEqualTo("1");
        await Assert.That(HashCodecUserHashMapper.GetKey(model).ToString()).IsEqualTo("user:{ü:{}}:ü:{}");
    }

    [Test]
    [NotInParallel]
    public async Task NumericAndTimestampEncodingsIgnoreCurrentCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var model = Create();
            var fields = HashCodecUserHashMapper.ToFields(model);
            await Assert.That(fields["Score"]).IsEqualTo("1.125");
            await Assert.That(fields["Balance"]).IsEqualTo("1234.50");
            await Assert.That(fields["Created"]).IsEqualTo("2026-10-08T15:00:00.0000000+05:30");
            await Assert.That(HashCodecUserHashMapper.FromFields(fields)).IsEqualTo(model);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Test]
    public async Task UnknownFieldsAreIgnoredAndMutableModelsAreConstructedDirectly()
    {
        var fields = HashCodecMutableHashMapper.ToFields(new HashCodecMutable { Id = "a", Value = 5 });
        fields["future"] = "ignored";
        var model = HashCodecMutableHashMapper.FromFields(fields);
        await Assert.That(model.Id).IsEqualTo("a");
        await Assert.That(model.Value).IsEqualTo(5);
    }

    [Test]
    [Arguments("Name", null)]
    [Arguments("Count", "not a number")]
    [Arguments("Age", "2147483648")]
    [Arguments("Active", "true")]
    [Arguments("Token", "bad-guid")]
    [Arguments("Created", "2026-10-08")]
    public async Task MissingOrMalformedFieldsFail(string field, string? replacement)
    {
        var fields = HashCodecUserHashMapper.ToFields(Create());
        if (replacement is null) fields.Remove(field);
        else fields[field] = replacement;
        var failed = false;
        try { _ = HashCodecUserHashMapper.FromFields(fields); }
        catch (FormatException) { failed = true; }
        catch (OverflowException) { failed = true; }
        await Assert.That(failed).IsTrue();
    }

    [Test]
    [Arguments("Age")]
    [Arguments("Count")]
    [Arguments("Score")]
    [Arguments("Balance")]
    public async Task NumericFieldsRejectWhitespaceAndThousandsSeparators(string field)
    {
        foreach (var text in new[] { "1,5", "1,000", " 5", "5 ", "\t5", "5\r\n" })
        {
            var fields = HashCodecUserHashMapper.ToFields(Create(age: 5));
            fields[field] = text;
            await Assert.That(() => HashCodecUserHashMapper.FromFields(fields)).Throws<FormatException>();
        }
    }

    [Test]
    [Arguments("Id")]
    [Arguments("Price")]
    [Arguments("Count")]
    [Arguments("Score")]
    [Arguments("Amount")]
    public async Task NullableModelNumericFieldsUseTheSameStrictParsing(string field)
    {
        foreach (var text in new[] { "1,5", "1,000", " 5", "5 " })
        {
            var fields = new Dictionary<string, string> { ["Id"] = "1", ["Price"] = "2" };
            fields[field] = text;
            await Assert.That(() => HashCodecNullableHashMapper.FromFields(fields)).Throws<FormatException>();
        }
    }

    [Test]
    [Arguments(double.MinValue)]
    [Arguments(double.MaxValue)]
    [Arguments(double.Epsilon)]
    [Arguments(double.NaN)]
    [Arguments(double.PositiveInfinity)]
    [Arguments(double.NegativeInfinity)]
    public async Task DoubleRoundTripFormatsRemainReadable(double score)
    {
        var model = Create(age: int.MinValue) with { Count = long.MinValue, Score = score, Balance = decimal.MinValue };
        await Assert.That(HashCodecUserHashMapper.FromFields(HashCodecUserHashMapper.ToFields(model))).IsEqualTo(model);
    }

    [Test]
    [Arguments(" NaN")]
    [Arguments("NaN ")]
    [Arguments(" Infinity")]
    [Arguments("-Infinity\t")]
    public async Task SpecialDoubleValuesRejectSurroundingWhitespace(string text)
    {
        var fields = HashCodecUserHashMapper.ToFields(Create());
        fields["Score"] = text;
        await Assert.That(() => HashCodecUserHashMapper.FromFields(fields)).Throws<FormatException>();
    }

    [Test]
    public async Task SignedNumbersAndFloatingPointExponentsAreReadable()
    {
        var fields = HashCodecUserHashMapper.ToFields(Create());
        fields["Age"] = "+5";
        fields["Count"] = "-5";
        fields["Score"] = "-1.5E+2";
        fields["Balance"] = "+1.5E-2";
        var model = HashCodecUserHashMapper.FromFields(fields);
        await Assert.That(model.Age).IsEqualTo(5);
        await Assert.That(model.Count).IsEqualTo(-5);
        await Assert.That(model.Score).IsEqualTo(-150d);
        await Assert.That(model.Balance).IsEqualTo(0.015M);
    }

    [Test]
    public async Task RuntimeNullForRequiredStringCannotBeWrittenOrUsedAsKey()
    {
        var invalid = Create() with { Id = null! };
        await Assert.That(() => HashCodecUserHashMapper.ToFields(invalid)).Throws<ArgumentException>();
        await Assert.That(() => HashCodecUserHashMapper.GetKey(invalid)).Throws<ArgumentException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EveryNullableScalarRoundTrips(bool hasValues)
    {
        var user = Create();
        var model = hasValues
            ? new HashCodecNullable(-7, 1.25M, false, long.MinValue, double.NegativeInfinity, decimal.MaxValue, user.Token, user.Created)
            : new HashCodecNullable(-7, 1.25M, null, null, null, null, null, null);
        var fields = HashCodecNullableHashMapper.ToFields(model);
        await Assert.That(HashCodecNullableHashMapper.FromFields(fields)).IsEqualTo(model);
        await Assert.That(fields.Count).IsEqualTo(hasValues ? 8 : 2);
        await Assert.That(HashCodecNullableHashMapper.GetKey(model).ToString()).IsEqualTo("number:-7:1.25");
    }

    [Test]
    public async Task NullArgumentsFailAtTheCodecBoundary()
    {
        await Assert.That(() => HashCodecUserHashMapper.GetKey(null!)).Throws<ArgumentNullException>();
        await Assert.That(() => HashCodecUserHashMapper.ToFields(null!)).Throws<ArgumentNullException>();
        await Assert.That(() => HashCodecUserHashMapper.FromFields(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task ConstructorBoundPropertyDoesNotInvokeSetterTwice()
    {
        var model = HashCodecConstructorHashMapper.FromFields(new Dictionary<string, string> { ["Id"] = "5" });
        await Assert.That(model.Id).IsEqualTo(5);
        await Assert.That(model.CountWrites()).IsEqualTo(1);
    }
}
