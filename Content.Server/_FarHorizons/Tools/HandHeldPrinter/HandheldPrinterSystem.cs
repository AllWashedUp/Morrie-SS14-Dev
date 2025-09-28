using Robust.Shared.Timing;
using Robust.Shared.Prototypes;
using Content.Server.Administration.Logs;
using Robust.Server.Containers;
using Robust.Shared.Audio.Systems;
using Robust.Server.GameObjects;
using Content.Server.Materials;
using Content.Server.Popups;
using Content.Server.Stack;
using Content.Shared.Materials;
using Content.Shared.Power;
using Content.Shared.Lathe;
using Content.Shared.Research.Components;
using Content.Shared.UserInterface;
using Content.Shared._FarHorizons.Tools.HandheldPrinter;
using System.Diagnostics.CodeAnalysis;
using Content.Shared.Research.Prototypes;
using System.Linq;
using Content.Server.Power.EntitySystems;
using Content.Shared.Lathe.Prototypes;
using Content.Shared.Database;
using JetBrains.Annotations;
using Robust.Shared.Log;
using Content.Server.Lathe.Components;

namespace Content.Server._FarHorizons.Tools.HandheldPrinter.Systems;

public sealed class HandheldPrinterSystem : HandheldPrinterSharedSystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IAdminLogManager _adminLogger = default!;
    [Dependency] private readonly SharedAppearanceSystem _appearance = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly ContainerSystem _container = default!;
    [Dependency] private readonly UserInterfaceSystem _uiSys = default!;
    [Dependency] private readonly MaterialStorageSystem _materialStorage = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly StackSystem _stack = default!;
    [Dependency] private readonly TransformSystem _transform = default!;


    public override void Initialize()
    {
        Log.Debug("initialize");
        base.Initialize();
        SubscribeLocalEvent<HandheldPrinterComponent, GetMaterialWhitelistEvent>(OnGetWhitelist);
        SubscribeLocalEvent<HandheldPrinterComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<HandheldPrinterComponent, PowerChangedEvent>(OnPowerChanged);
        SubscribeLocalEvent<HandheldPrinterComponent, TechnologyDatabaseModifiedEvent>(OnDatabaseModified);
        SubscribeLocalEvent<HandheldPrinterComponent, ResearchRegistrationChangedEvent>(OnResearchRegistrationChanged);

        SubscribeLocalEvent<HandheldPrinterComponent, LatheQueueRecipeMessage>(OnLatheQueueRecipeMessage);
        SubscribeLocalEvent<HandheldPrinterComponent, LatheSyncRequestMessage>(OnLatheSyncRequestMessage);

        SubscribeLocalEvent<HandheldPrinterComponent, BeforeActivatableUIOpenEvent>((u, c, _) => UpdateUserInterfaceState(u, c));
        SubscribeLocalEvent<HandheldPrinterComponent, MaterialAmountChangedEvent>(OnMaterialAmountChanged);
        SubscribeLocalEvent<TechnologyDatabaseComponent, Shared._FarHorizons.Tools.HandheldPrinter.LatheGetRecipesEvent>(OnGetRecipes);




    }
    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<LatheProducingComponent, HandheldPrinterComponent>();
        while (query.MoveNext(out var uid, out var comp, out var lathe))
        {
            if (lathe.CurrentRecipe == null)
                continue;

            if (_timing.CurTime - comp.StartTime >= comp.ProductionLength)
                FinishProducing(uid, lathe);
        }
    }

    private void OnGetWhitelist(EntityUid uid, HandheldPrinterComponent component, ref GetMaterialWhitelistEvent args)
    {

        if (args.Storage != uid)
            return;
        var materialWhitelist = new List<ProtoId<MaterialPrototype>>();
        var recipes = GetAvailableRecipes(uid, component, true);
        foreach (var id in recipes)
        {
            if (!_proto.TryIndex(id, out var proto))
                continue;
            foreach (var (mat, _) in proto.Materials)
            {
                if (!materialWhitelist.Contains(mat))
                {
                    materialWhitelist.Add(mat);
                }
            }
        }

        var combined = args.Whitelist.Union(materialWhitelist).ToList();
        args.Whitelist = combined;
    }

    [PublicAPI]
    public bool TryGetAvailableRecipes(EntityUid uid, [NotNullWhen(true)] out List<ProtoId<LatheRecipePrototype>>? recipes, [NotNullWhen(true)] HandheldPrinterComponent? component = null, bool getUnavailable = false)
    {
        recipes = null;
        if (!Resolve(uid, ref component))
            return false;
        recipes = GetAvailableRecipes(uid, component, getUnavailable);
        return true;
    }

    public List<ProtoId<LatheRecipePrototype>> GetAvailableRecipes(EntityUid uid, HandheldPrinterComponent component, bool getUnavailable = false)
    {
        var ev = new Shared._FarHorizons.Tools.HandheldPrinter.LatheGetRecipesEvent((uid, component), getUnavailable);
        Log.Debug($"{component.StaticPacks}");
        AddRecipesFromPacks(ev.Recipes, component.StaticPacks);
        RaiseLocalEvent(uid, ev);
        return ev.Recipes.ToList();
    }

    public bool TryAddToQueue(EntityUid uid, LatheRecipePrototype recipe, HandheldPrinterComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return false;

        if (!CanProduce(uid, recipe, 1, component))
            return false;

        foreach (var (mat, amount) in recipe.Materials)
        {
            var adjustedAmount = recipe.ApplyMaterialDiscount
                ? (int)(-amount * component.MaterialUseMultiplier)
                : -amount;

            _materialStorage.TryChangeMaterialAmount(uid, mat, adjustedAmount);
        }
        component.Queue.Enqueue(recipe);

        return true;
    }

    public bool TryStartProducing(EntityUid uid, HandheldPrinterComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return false;
        if (component.CurrentRecipe != null || component.Queue.Count <= 0 || !this.IsPowered(uid, EntityManager))
            return false;

        var recipeProto = component.Queue.Dequeue();
        var recipe = _proto.Index(recipeProto);

        var time = recipe.CompleteTime * component.TimeMultiplier;

        var lathe = EnsureComp<LatheProducingComponent>(uid);
        lathe.StartTime = _timing.CurTime;
        lathe.ProductionLength = time;
        component.CurrentRecipe = recipe;

        var ev = new Shared._FarHorizons.Tools.HandheldPrinter.LatheStartPrintingEvent(recipe);
        RaiseLocalEvent(uid, ref ev);

        _audio.PlayPvs(component.ProducingSound, uid);
        UpdateRunningAppearance(uid, true);
        UpdateUserInterfaceState(uid, component);

        if (time == TimeSpan.Zero)
        {
            FinishProducing(uid, component, lathe);
        }
        return true;
    }

    public void FinishProducing(EntityUid uid, HandheldPrinterComponent? comp = null, LatheProducingComponent? prodComp = null)
    {
        if (!Resolve(uid, ref comp, ref prodComp, false))
            return;

        if (comp.CurrentRecipe != null)
        {
            var currentRecipe = _proto.Index(comp.CurrentRecipe.Value);
            if (currentRecipe.Result is { } resultProto)
            {
                var result = SpawnAtPosition(resultProto, Transform(uid).Coordinates);//Spawn(resultProto, Transform(uid).Coordinates);
                _stack.TryMergeToContacts(result);
                if (currentRecipe.PrintTicket)
                {
                    var tickets = SpawnAtPosition(currentRecipe.TicketProtoId, Transform(uid).Coordinates);
                    _stack.TryMergeToContacts(tickets);
                }
            }

        }

        comp.CurrentRecipe = null;
        prodComp.StartTime = _timing.CurTime;

        if (!TryStartProducing(uid, comp))
        {
            RemCompDeferred(uid, prodComp);
            UpdateUserInterfaceState(uid, comp);
            UpdateRunningAppearance(uid, false);
        }
    }
    public void UpdateUserInterfaceState(EntityUid uid, HandheldPrinterComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        var producing = component.CurrentRecipe;
        if (producing == null && component.Queue.TryPeek(out var next))
            producing = next;

        var state = new LatheUpdateState(GetAvailableRecipes(uid, component), component.Queue.ToArray(), producing);
        _uiSys.SetUiState(uid, LatheUiKey.Key, state);
    }

    /// <summary>
    /// Adds every unlocked recipe from each pack to the recipes list.
    /// </summary>
    public void AddRecipesFromDynamicPacks(ref Shared._FarHorizons.Tools.HandheldPrinter.LatheGetRecipesEvent args, TechnologyDatabaseComponent database, IEnumerable<ProtoId<LatheRecipePackPrototype>> packs)
    {
        foreach (var id in packs)
        {
            var pack = _proto.Index(id);
            foreach (var recipe in pack.Recipes)
            {
                if (args.GetUnavailable || database.UnlockedRecipes.Contains(recipe))
                    args.Recipes.Add(recipe);
            }
        }
    }

    private void OnGetRecipes(EntityUid uid, TechnologyDatabaseComponent component, Shared._FarHorizons.Tools.HandheldPrinter.LatheGetRecipesEvent args)
    {
        if (uid == args.Lathe)
        {
            AddRecipesFromDynamicPacks(ref args, component, args.Comp.DynamicPacks);
        }

    }

    private void OnMaterialAmountChanged(EntityUid uid, HandheldPrinterComponent component, ref MaterialAmountChangedEvent args)
    {
        UpdateUserInterfaceState(uid, component);
    }
    private void OnMapInit(EntityUid uid, HandheldPrinterComponent component, MapInitEvent args)
    {
        _appearance.SetData(uid, LatheVisuals.IsInserting, false);
        _appearance.SetData(uid, LatheVisuals.IsRunning, false);

        _materialStorage.UpdateMaterialWhitelist(uid);
    }

    /// <summary>
    /// Sets the machine sprite to either play the running animation
    /// or stop.
    /// </summary>
    private void UpdateRunningAppearance(EntityUid uid, bool isRunning)
    {
        _appearance.SetData(uid, LatheVisuals.IsRunning, isRunning);
    }

    private void OnPowerChanged(EntityUid uid, HandheldPrinterComponent component, ref PowerChangedEvent args)
    {
        if (!args.Powered)
        {
            RemComp<LatheProducingComponent>(uid);
            UpdateRunningAppearance(uid, false);
        }
        else if (component.CurrentRecipe != null)
        {
            EnsureComp<LatheProducingComponent>(uid);
            TryStartProducing(uid, component);
        }
    }

    private void OnDatabaseModified(EntityUid uid, HandheldPrinterComponent component, ref TechnologyDatabaseModifiedEvent args)
    {
        UpdateUserInterfaceState(uid, component);
    }

    private void OnResearchRegistrationChanged(EntityUid uid, HandheldPrinterComponent component, ref ResearchRegistrationChangedEvent args)
    {
        UpdateUserInterfaceState(uid, component);
    }

    protected override bool HasRecipe(EntityUid uid, LatheRecipePrototype recipe, HandheldPrinterComponent component)
    {
        return GetAvailableRecipes(uid, component).Contains(recipe.ID);
    }

    #region UI Messages

    private void OnLatheQueueRecipeMessage(EntityUid uid, HandheldPrinterComponent component, LatheQueueRecipeMessage args)
    {
        if (_proto.TryIndex(args.ID, out LatheRecipePrototype? recipe))
        {
            var count = 0;
            for (var i = 0; i < args.Quantity; i++)
            {
                if (TryAddToQueue(uid, recipe, component))
                    count++;
                else
                    break;
            }
            if (count > 0)
            {
                _adminLogger.Add(LogType.Action,
                    LogImpact.Low,
                    $"{ToPrettyString(args.Actor):player} queued {count} {GetRecipeName(recipe)} at {ToPrettyString(uid):lathe}");
            }
        }
        TryStartProducing(uid, component);
        UpdateUserInterfaceState(uid, component);
    }

    private void OnLatheSyncRequestMessage(EntityUid uid, HandheldPrinterComponent component, LatheSyncRequestMessage args)
    {
        UpdateUserInterfaceState(uid, component);
    }
    #endregion
}


        


