using System.CommandLine;
using System.Text;
using System.Text.Json.Nodes;
using Updatr.Core.Carts;
using Updatr.Core.Catalog;
using Updatr.Core.Packages;

namespace Updatr.Cli;

/// <summary>Commands for the catalog, installs, carts, and templates (Phase 5).</summary>
internal static class CartCommands
{
    public static IEnumerable<Command> All() => [CatalogCommand(), InstallCommand(), CartCommand(), TemplateCommand()];

    private static Command CatalogCommand()
    {
        Option<string?> category = new("--category") { Description = $"Only this section ({string.Join(", ", CatalogSections.Known.Select(s => s.Key))})." };
        Option<string?> search = new("--search", "-s") { Description = "Match name, id, or description." };
        Command list = new("list", "List the apps Updatr can install.") { category, search };

        list.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, async (host, sink) =>
        {
            IReadOnlyList<CatalogApp> apps = await host.CatalogList.RunAsync(new CatalogQuery(parse.GetValue(category), parse.GetValue(search)), sink, ct).ConfigureAwait(false);
            StringBuilder text = new();

            foreach (IGrouping<string, CatalogApp> group in apps.GroupBy(CatalogSections.SectionOf))
            {
                text.AppendLine($"{CatalogSections.Describe(group.Key).Title} ({group.Key})");

                foreach (CatalogApp app in group)
                    text.AppendLine($"  {app.Id,-24} {app.Name,-28} {string.Join(", ", app.Sources.Select(s => s.SourceId))}");
            }

            sink.Output(text.ToString().TrimEnd(), new JsonArray([.. apps.Select(a => (JsonNode)new JsonObject
            {
                ["id"] = a.Id,
                ["name"] = a.Name,
                ["category"] = a.Category,
                ["section"] = CatalogSections.SectionOf(a),
                ["description"] = a.Description,
                ["sources"] = new JsonArray([.. a.Sources.Select(s => (JsonNode)JsonValue.Create(s.SourceId)!)]),
            })]));
            return CliApp.ExitCodes.Ok;
        }));

        Command catalog = new("catalog", "The apps Updatr knows how to install.");
        catalog.Subcommands.Add(list);
        return catalog;
    }

    private static Command InstallCommand()
    {
        Argument<string[]> apps = new("apps") { Description = "Catalog app ids (see: catalog list).", Arity = ArgumentArity.ZeroOrMore };
        Option<string[]> templates = new("--template", "-t") { Description = "Add a template's apps (repeatable).", AllowMultipleArgumentsPerToken = true };
        Option<string[]> carts = new("--cart", "-c") { Description = "Add a saved cart's apps (repeatable).", AllowMultipleArgumentsPerToken = true };
        Option<bool> includeInstalled = new("--include-installed") { Description = "Install even apps that are already installed." };
        Command command = new("install", "Install apps, templates, and saved carts together. Missing prerequisites (Node for npm packages, ...) are added and installed first.")
        {
            apps, templates, carts, includeInstalled,
        };

        command.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, async (host, sink) =>
        {
            InstallRequest request = new()
            {
                AppIds = parse.GetValue(apps) ?? [],
                Templates = parse.GetValue(templates) ?? [],
                Carts = parse.GetValue(carts) ?? [],
                IncludeInstalled = parse.GetValue(includeInstalled),
            };

            if (request.AppIds.Count + request.Templates.Count + request.Carts.Count == 0)
            {
                sink.Error("Name apps, --template, or --cart to install.");
                return CliApp.ExitCodes.Usage;
            }

            RunSummary summary = await host.Install.RunAsync(request, sink, ct).ConfigureAwait(false);
            sink.Summary(summary);
            return summary.HasFailures ? CliApp.ExitCodes.Failed : CliApp.ExitCodes.Ok;
        }));

        return command;
    }

    private static Command CartCommand()
    {
        Command list = new("list", "List saved carts.");
        list.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, async (host, sink) =>
        {
            IReadOnlyList<string> names = await host.CartList.RunAsync(true, sink, ct).ConfigureAwait(false);
            sink.Output(names.Count == 0 ? "No saved carts." : string.Join(Environment.NewLine, names), new JsonArray([.. names.Select(n => (JsonNode)JsonValue.Create(n)!)]));
            return CliApp.ExitCodes.Ok;
        }));

        Argument<string> showName = new("name");
        Command show = new("show", "Show a saved cart.") { showName };
        show.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, async (host, sink) =>
        {
            Cart cart = await host.CartShow.RunAsync(parse.GetValue(showName)!, sink, ct).ConfigureAwait(false);
            sink.Output(Describe(cart), Json(cart));
            return CliApp.ExitCodes.Ok;
        }));

        Argument<string> saveName = new("name") { Description = "Cart name. Saving over an existing cart replaces it." };
        Argument<string[]> saveApps = new("apps") { Arity = ArgumentArity.ZeroOrMore, Description = "Catalog app ids." };
        Option<string[]> saveTemplates = new("--template", "-t") { AllowMultipleArgumentsPerToken = true, Description = "Add a template's apps." };
        Option<string[]> saveCarts = new("--cart", "-c") { AllowMultipleArgumentsPerToken = true, Description = "Merge in a saved cart." };
        Command save = new("save", "Build a cart from apps, templates, and other carts (merged, no duplicates) and save it.") { saveName, saveApps, saveTemplates, saveCarts };
        save.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, async (host, sink) =>
        {
            Cart cart = await host.CartSave.RunAsync(new SaveCartRequest(parse.GetValue(saveName)!, parse.GetValue(saveApps) ?? [], parse.GetValue(saveTemplates) ?? [], parse.GetValue(saveCarts) ?? []), sink, ct).ConfigureAwait(false);
            sink.Output(Describe(cart), Json(cart));
            return CliApp.ExitCodes.Ok;
        }));

        Argument<string> removeName = new("name");
        Argument<string[]> removeApps = new("apps") { Arity = ArgumentArity.OneOrMore };
        Command remove = new("remove", "Remove apps from a saved cart.") { removeName, removeApps };
        remove.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, async (host, sink) =>
        {
            Cart cart = await host.CartRemove.RunAsync(new RemoveFromCartRequest(parse.GetValue(removeName)!, parse.GetValue(removeApps)!), sink, ct).ConfigureAwait(false);
            sink.Output(Describe(cart), Json(cart));
            return CliApp.ExitCodes.Ok;
        }));

        Argument<string> deleteName = new("name");
        Command delete = new("delete", "Delete a saved cart.") { deleteName };
        delete.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, async (host, sink) =>
        {
            await host.CartDelete.RunAsync(parse.GetValue(deleteName)!, sink, ct).ConfigureAwait(false);
            sink.Result(true, $"Deleted cart '{parse.GetValue(deleteName)}'.");
            return CliApp.ExitCodes.Ok;
        }));

        Command cart = new("cart", "Saved carts: named lists of apps to install together.");
        foreach (Command c in new[] { list, show, save, remove, delete })
            cart.Subcommands.Add(c);
        return cart;
    }

    private static Command TemplateCommand()
    {
        Command list = new("list", "List templates - built in (read-only) and yours.");
        list.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, async (host, sink) =>
        {
            IReadOnlyList<TemplateInfo> templates = await host.TemplateList.RunAsync(true, sink, ct).ConfigureAwait(false);
            string text = string.Join(Environment.NewLine, templates.Select(t => $"  {t.Template.Id,-20} {t.Template.Name,-24} {t.Template.Items.Count,3} apps  {(t.BuiltIn ? "built in" : "yours")}"));
            sink.Output(text, new JsonArray([.. templates.Select(t => (JsonNode)new JsonObject { ["id"] = t.Template.Id, ["name"] = t.Template.Name, ["builtIn"] = t.BuiltIn, ["items"] = new JsonArray([.. t.Template.Items.Select(i => (JsonNode)JsonValue.Create(i)!)]) })]));
            return CliApp.ExitCodes.Ok;
        }));

        Argument<string> showId = new("id");
        Command show = new("show", "Show a template's apps.") { showId };
        show.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, async (host, sink) =>
        {
            TemplateInfo info = await host.TemplateShow.RunAsync(parse.GetValue(showId)!, sink, ct).ConfigureAwait(false);
            sink.Output($"{info.Template.Name} ({(info.BuiltIn ? "built in" : "yours")}){Environment.NewLine}{string.Join(Environment.NewLine, info.Template.Items.Select(i => "  " + i))}",
                new JsonObject { ["id"] = info.Template.Id, ["name"] = info.Template.Name, ["builtIn"] = info.BuiltIn, ["items"] = new JsonArray([.. info.Template.Items.Select(i => (JsonNode)JsonValue.Create(i)!)]) });
            return CliApp.ExitCodes.Ok;
        }));

        Argument<string> saveId = new("id") { Description = "Your template's id (lowercase, hyphens). Saving over your own replaces it." };
        Argument<string[]> saveApps = new("apps") { Arity = ArgumentArity.ZeroOrMore };
        Option<string?> saveName = new("--name") { Description = "Display name (defaults to the id)." };
        Option<string?> fromCart = new("--from-cart") { Description = "Start from a saved cart's apps." };
        Command save = new("save", "Save one of your templates from apps and/or a saved cart.") { saveId, saveApps, saveName, fromCart };
        save.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, async (host, sink) =>
        {
            List<string> appIds = [.. parse.GetValue(saveApps) ?? []];

            if (parse.GetValue(fromCart) is { } cartName)
                appIds.InsertRange(0, (await host.CartShow.RunAsync(cartName, sink, ct).ConfigureAwait(false)).Items.Where(i => !i.Automatic).Select(i => i.AppId));

            string id = parse.GetValue(saveId)!;
            CatalogTemplate saved = await host.TemplateSave.RunAsync(new SaveTemplateRequest(id, parse.GetValue(saveName) ?? id, null, appIds), sink, ct).ConfigureAwait(false);
            sink.Result(true, $"Saved template '{saved.Id}' ({saved.Items.Count} apps).");
            return CliApp.ExitCodes.Ok;
        }));

        Argument<string> copyFrom = new("from");
        Option<string?> copyAs = new("--as") { Description = "Id for the copy (default: <from>-mine)." };
        Command copy = new("copy", "Copy a template (usually a built-in) to one of yours, to edit.") { copyFrom, copyAs };
        copy.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, async (host, sink) =>
        {
            CatalogTemplate copied = await host.TemplateCopy.RunAsync(new CopyTemplateRequest(parse.GetValue(copyFrom)!, parse.GetValue(copyAs)), sink, ct).ConfigureAwait(false);
            sink.Result(true, $"Copied to '{copied.Id}'.");
            return CliApp.ExitCodes.Ok;
        }));

        Argument<string> deleteId = new("id");
        Command delete = new("delete", "Delete one of your templates.") { deleteId };
        delete.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, async (host, sink) =>
        {
            await host.TemplateDelete.RunAsync(parse.GetValue(deleteId)!, sink, ct).ConfigureAwait(false);
            sink.Result(true, $"Deleted template '{parse.GetValue(deleteId)}'.");
            return CliApp.ExitCodes.Ok;
        }));

        Command template = new("template", "Templates: named sets of apps (Base, Dev, ...) - built in, or your own.");
        foreach (Command c in new[] { list, show, save, copy, delete })
            template.Subcommands.Add(c);
        return template;
    }

    private static string Describe(Cart cart)
        => $"{cart.Name} ({cart.Items.Count} apps){Environment.NewLine}{string.Join(Environment.NewLine, cart.Items.Select(i => $"  {i.AppId,-24} from {i.AddedBy ?? "you"}{(i.Source is null ? "" : $" via {i.Source}")}"))}";

    private static JsonObject Json(Cart cart) => new()
    {
        ["name"] = cart.Name,
        ["items"] = new JsonArray([.. cart.Items.Select(i => (JsonNode)new JsonObject { ["appId"] = i.AppId, ["addedBy"] = i.AddedBy, ["source"] = i.Source })]),
    };
}
