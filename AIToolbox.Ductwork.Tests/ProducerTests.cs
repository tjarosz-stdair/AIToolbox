using AIToolbox.Ductwork.Contracts;

namespace AIToolbox.Ductwork.Tests;

public class ProducerTests
{
    [Fact]
    public void Llm_producer_requires_both_snapshots()
    {
        Assert.Empty(ProducerRef.Llm(1, 2).Validate());
        Assert.NotEmpty(new ProducerRef { Kind = ProducerKind.Llm, DirectiveSnapshotId = 1 }.Validate());
        Assert.NotEmpty(new ProducerRef { Kind = ProducerKind.Llm, ProviderSnapshotId = 2 }.Validate());
        Assert.NotEmpty(new ProducerRef { Kind = ProducerKind.Llm }.Validate());
    }

    [Fact]
    public void Algorithm_producer_requires_name_version_and_params_hash()
    {
        Assert.Empty(ProducerRef.Algorithm("hvac_takeoff", "1.1-audit", "abc123").Validate());
        Assert.NotEmpty(ProducerRef.Algorithm("", "1", "h").Validate());
        Assert.NotEmpty(ProducerRef.Algorithm("n", " ", "h").Validate());
        Assert.NotEmpty(ProducerRef.Algorithm("n", "1", "").Validate());
    }

    [Fact]
    public void Manual_producer_requires_user()
    {
        Assert.Empty(ProducerRef.Manual("a@example.com").Validate());
        Assert.NotEmpty(ProducerRef.Manual("").Validate());
    }

    [Fact]
    public void Producer_shapes_cannot_be_mixed()
    {
        Assert.NotEmpty(new ProducerRef { Kind = ProducerKind.Algorithm, AlgorithmName = "n", AlgorithmVersion = "1", ParamsHash = "h", ProviderSnapshotId = 3 }.Validate());
        Assert.NotEmpty(new ProducerRef { Kind = ProducerKind.Manual, UserEmail = "a@example.com", AlgorithmName = "n" }.Validate());
        Assert.NotEmpty(new ProducerRef { Kind = ProducerKind.Llm, DirectiveSnapshotId = 1, ProviderSnapshotId = 2, UserEmail = "a@example.com" }.Validate());
    }

    [Fact]
    public void Key_is_stable_and_normalized()
    {
        Assert.Equal(ProducerRef.Algorithm("Engine", "1.0", "ABC").Key(), ProducerRef.Algorithm(" engine ", "1.0", "abc").Key());
        Assert.Equal(ProducerRef.Manual("A@Example.com").Key(), ProducerRef.Manual("a@example.com").Key());
        Assert.NotEqual(ProducerRef.Llm(1, 2).Key(), ProducerRef.Llm(1, 3).Key());
    }

    [Fact]
    public void Key_of_an_invalid_producer_throws()
    {
        Assert.Throws<InvalidOperationException>(() => new ProducerRef { Kind = ProducerKind.Llm }.Key());
    }

    [Fact]
    public void Default_provenance_is_rejected()
    {
        Assert.False(default(Provenance).IsValid);
        Assert.Throws<InvalidOperationException>(() => default(Provenance).EnsureValid());
        Assert.Throws<ArgumentOutOfRangeException>(() => new Provenance(0, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Provenance(5, 0));
        new Provenance(1, 1).EnsureValid();
    }
}
