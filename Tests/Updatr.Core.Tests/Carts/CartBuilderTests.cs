using Updatr.Core.Carts;
using Updatr.Core.Catalog;

namespace Updatr.Core.Tests.Carts;

public sealed class CartBuilderTests
{
    private static readonly HashSet<string> NoTools = [];

    private static readonly AppCatalog Catalog = new(
        [
            App("git"),
            App("nodejs-lts", provides: ["node", "npm"]),
            App("vscode", provides: ["code"]),
            App("typescript", source: new NpmSourceRef { Package = "typescript" }),
            App("pnpm", source: new NpmSourceRef { Package = "pnpm" }),
            App("gitlens", source: new VsCodeExtensionSourceRef { Extension = "eamodio.gitlens" }),
            App("docker", requires: ["wsl"]),
            App("wsl"),
            App("orphan", source: new PipxSourceRef { Package = "black" }),
        ],
        [new CatalogTemplate { Id = "dev", Name = "Dev", Items = ["git", "typescript", "vscode"] }],
        []);

    private static CatalogApp App(string id, IReadOnlyList<string>? provides = null, PackageSourceRef? source = null, IReadOnlyList<string>? requires = null) => new()
    {
        Id = id,
        Name = id,
        Category = "dev",
        Sources = [source ?? new WingetSourceRef { Id = $"Vendor.{id}" }],
        Provides = provides ?? [],
        Requires = requires ?? [],
    };

    private static string[] Order(CartPlan plan) => [.. plan.Items.Select(i => i.App.Id)];

    [Fact]
    public void TemplatesAndPicksMergeWithoutDuplicates()
    {
        Cart cart = CartBuilder.AddTemplate(CartBuilder.Add(Cart.Empty(), "git"), Catalog.FindTemplate("dev")!);

        cart.Items.Select(i => i.AppId).Should().Equal("git", "typescript", "vscode");
        cart.Items.Single(i => i.AppId == "git").AddedBy.Should().Be(CartBuilder.AddedByYou, "the first pick is kept");
    }

    [Fact]
    public void MergingCartsUnionsThemAndTheFirstCartsChoicesWin()
    {
        Cart first = CartBuilder.Add(Cart.Empty("a"), "git", source: "winget");
        Cart second = CartBuilder.Add(CartBuilder.Add(Cart.Empty("b"), "git", source: "directUrl"), "vscode");

        Cart merged = CartBuilder.Merge(first, second);

        merged.Items.Select(i => i.AppId).Should().Equal("git", "vscode");
        merged.Items[0].Source.Should().Be("winget");
        merged.Items[1].AddedBy.Should().Be(CartBuilder.AddedByYou, "carried over from cart b's own item");
    }

    [Fact]
    public void AMissingToolPullsInItsProviderFirstAndSaysWhy()
    {
        Cart cart = CartBuilder.Add(CartBuilder.Add(Cart.Empty(), "typescript"), "pnpm");

        (Cart resolved, CartPlan plan) = CartBuilder.Resolve(cart, Catalog, NoTools);

        Order(plan).Should().Equal("nodejs-lts", "typescript", "pnpm");
        CartItem node = resolved.Items.Single(i => i.AppId == "nodejs-lts");
        node.Automatic.Should().BeTrue();
        node.RequiredBy.Should().Equal("pnpm", "typescript");
    }

    [Fact]
    public void AToolAlreadyOnTheMachineAddsNothing()
    {
        (_, CartPlan plan) = CartBuilder.Resolve(CartBuilder.Add(Cart.Empty(), "typescript"), Catalog, new HashSet<string> { "npm" });

        Order(plan).Should().Equal("typescript");
    }

    [Fact]
    public void AProviderAlreadyInTheCartIsUsedAndMovedFirst()
    {
        Cart cart = CartBuilder.Add(CartBuilder.Add(Cart.Empty(), "gitlens"), "vscode");

        (Cart resolved, CartPlan plan) = CartBuilder.Resolve(cart, Catalog, NoTools);

        Order(plan).Should().Equal("vscode", "gitlens");
        resolved.Items.Single(i => i.AppId == "vscode").Automatic.Should().BeFalse("it was picked, not added");
    }

    [Fact]
    public void ExplicitRequiresAreAddedToo()
    {
        (_, CartPlan plan) = CartBuilder.Resolve(CartBuilder.Add(Cart.Empty(), "docker"), Catalog, NoTools);

        Order(plan).Should().Equal("wsl", "docker");
    }

    [Fact]
    public void RemovingTheLastDependentDropsItsPrerequisiteOnResolve()
    {
        (Cart resolved, _) = CartBuilder.Resolve(CartBuilder.Add(Cart.Empty(), "typescript"), Catalog, NoTools);

        (Cart after, CartPlan plan) = CartBuilder.Resolve(CartBuilder.Remove(resolved, "typescript"), Catalog, NoTools);

        after.Items.Should().BeEmpty();
        plan.Items.Should().BeEmpty();
    }

    [Fact]
    public void PickingAnAutomaticItemMakesItAPick()
    {
        (Cart resolved, _) = CartBuilder.Resolve(CartBuilder.Add(Cart.Empty(), "typescript"), Catalog, NoTools);

        Cart picked = CartBuilder.Add(resolved, "nodejs-lts");
        (Cart after, _) = CartBuilder.Resolve(CartBuilder.Remove(picked, "typescript"), Catalog, NoTools);

        after.Items.Should().ContainSingle().Which.AppId.Should().Be("nodejs-lts");
    }

    [Fact]
    public void UnknownAppsAndUnprovidedToolsAreReportedNotThrown()
    {
        Cart cart = CartBuilder.Add(CartBuilder.Add(Cart.Empty(), "not-a-thing"), "orphan");

        (_, CartPlan plan) = CartBuilder.Resolve(cart, Catalog, NoTools);

        plan.Problems.Should().HaveCount(2)
            .And.Contain(p => p.Contains("'not-a-thing' is not in the catalog", StringComparison.Ordinal))
            .And.Contain(p => p.Contains("needs pipx", StringComparison.Ordinal));
        Order(plan).Should().Equal(["orphan"], "the app is still listed; its install will say what is missing");
    }

    [Theory]
    [InlineData("My Dev Box!", "my-dev-box")]
    [InlineData("  Gaming   PC  ", "gaming-pc")]
    [InlineData("???", "cart")]
    public void NamesBecomeSafeFileNames(string name, string slug)
    {
        CartStore.Slug(name).Should().Be(slug);
    }
}
