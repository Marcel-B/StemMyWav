using StemMyWav.Api.Catalog;

namespace StemMyWav.Api.Tests;

/// <summary>Prüft die eingebettete models.json. Der Gateway liest dieselbe Datei, ein Fehler
/// darin träfe also beide Dienste — deshalb steht die Prüfung in der CI und nicht im Betrieb.</summary>
public sealed class ModelCatalogTests
{
    private static readonly ModelCatalog Catalog = ModelCatalog.Load();

    [Fact]
    public void Loads_every_model_from_the_embedded_file()
    {
        Assert.NotEmpty(Catalog.Models);
        Assert.All(Catalog.Models, model =>
        {
            Assert.NotEmpty(model.Id);
            Assert.NotEmpty(model.Name);
            Assert.NotEmpty(model.Filename);
        });
    }

    [Fact]
    public void Resolves_the_default_of_the_file_and_a_configured_one()
    {
        Assert.Equal(Catalog.DefaultId, Catalog.ResolveDefault(null).Id);
        Assert.Equal("htdemucs-6s", Catalog.ResolveDefault("htdemucs-6s").Id);
    }

    [Fact]
    public void Refuses_a_default_that_is_not_in_the_catalog()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Catalog.ResolveDefault("does-not-exist"));
        Assert.Contains("does-not-exist", error.Message);
    }

    [Fact]
    public void Gives_every_model_at_least_two_distinct_stems()
    {
        Assert.All(Catalog.Models, model =>
        {
            Assert.True(model.Stems.Count >= 2, $"{model.Id} nennt weniger als zwei Stems.");
            Assert.Equal(model.Stems.Count, model.Stems.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        });
    }

    [Fact]
    public void Keeps_the_measured_realtime_factor_and_the_speed_class_consistent()
    {
        // Die Klassen sind die Entscheidungshilfe in der Modellliste; sie dürfen der Messung
        // nicht widersprechen, sonst wählt ein Aufrufer nach einer falschen Angabe.
        foreach (var model in Catalog.Models.Where(model => model.RealtimeFactor is not null))
        {
            var expected = model.RealtimeFactor switch
            {
                >= 4 => ModelSpeed.Fast,
                >= 1.5 => ModelSpeed.Moderate,
                >= 0.7 => ModelSpeed.Slow,
                _ => ModelSpeed.VerySlow
            };
            Assert.Equal(expected, model.Speed);
        }
    }

    [Fact]
    public void Marks_exactly_the_models_with_a_measurement_as_measured()
    {
        Assert.All(Catalog.Models, model => Assert.Equal(model.RealtimeFactor is not null, model.Measured));
    }

    [Fact]
    public void Finds_a_model_regardless_of_case_and_surrounding_space()
    {
        Assert.Equal("htdemucs-6s", Catalog.Find(" HTDemucs-6s ")?.Id);
        Assert.Null(Catalog.Find(null));
        Assert.Null(Catalog.Find(""));
    }
}
