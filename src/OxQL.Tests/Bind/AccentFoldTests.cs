using System.Text.RegularExpressions;
using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Core.Models;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// The pattern <c>contains</c> and <c>endsWith</c> run as folds accents the way the
/// request's collation does. Each letter is the class of the characters that share its primary
/// weight, <c>ss</c> and <c>ß</c> stand for each other, everything else is escaped. At a strength
/// that tells accents apart the pattern is the escaped text.
/// </summary>
public class AccentFoldTests
{
    private static readonly CollationOptions German = new();

    private static bool Finds(string operand, string stored) =>
        Regex.IsMatch(stored, AccentFold.Pattern(operand, German), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void A_letter_is_the_class_of_its_equivalents_and_anything_else_is_escaped()
    {
        AccentFold.Pattern("a", German).Should().Be("[aÀ-Åà-åĀ-ąǍǎǞ-ǡǺǻȀ-ȃȦȧḀḁẠ-ặ]", "the class is read from the collation over the Latin blocks; three or more neighbours are a range");
        AccentFold.Pattern("U", German).Should().Be(AccentFold.Pattern("u", German), "an upper-case operand folds to the same class").And.StartWith("[uÙ-Üù-üŨ-ų");
        AccentFold.Pattern("ü", German).Should().Be(AccentFold.Pattern("u", German), "an accented operand finds the plain letter too");
        AccentFold.Pattern("1.5 (2)", German).Should().Be(@"1\.5\ \(2\)", "digits and punctuation match themselves, escaped");
        AccentFold.Pattern("", German).Should().BeEmpty();
    }

    [Theory]
    [InlineData("muller", "Müller")]
    [InlineData("MÜLL", "muller")]
    [InlineData("ull", "MÜLLER")]
    [InlineData("strasse", "Hauptstraße")]
    [InlineData("straße", "HAUPTSTRASSE")]
    [InlineData("francois", "François")]
    [InlineData("Šk", "skoda")]
    [InlineData("a.b", "A.B")]
    [InlineData("STRASSE", "straẞe")]
    [InlineData("aether", "Æther")]
    [InlineData("æther", "AETHER")]
    [InlineData("oeuvre", "Œuvre")]
    [InlineData("œuvre", "oeuvre")]
    [InlineData("ijs", "Ĳs")]
    [InlineData("office", "oﬃce")]
    [InlineData("oﬃce", "office")]
    [InlineData("office", "oﬀice")]
    [InlineData("office", "ofﬁce")]
    [InlineData("stinta", "ștință")]
    [InlineData("viet", "Việt")]
    public void An_operand_finds_the_stored_text_whatever_its_accents_and_case(string operand, string stored) =>
        Finds(operand, stored).Should().BeTrue();

    [Theory]
    [InlineData("muller", "Mueller")]
    [InlineData("a.b", "axb")]
    [InlineData("ss", "s")]
    [InlineData("thorn", "þorn")]
    [InlineData("ae", "ä")]
    [InlineData("s", "ß")]
    public void It_finds_nothing_the_collation_tells_apart(string operand, string stored) =>
        Finds(operand, stored).Should().BeFalse();

    [Fact]
    public void A_strength_that_tells_accents_apart_keeps_the_text_as_written()
    {
        var secondary = new CollationOptions { Strength = 2 };

        AccentFold.Applies(secondary).Should().BeFalse();
        AccentFold.Pattern("Müller.", secondary).Should().Be(@"Müller\.");
        AccentFold.Applies(German).Should().BeTrue();
    }
}
