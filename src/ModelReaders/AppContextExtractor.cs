// ============================================================================
// AIDE Lite - AI-powered IDE extension for Mendix Studio Pro
// Copyright (c) 2025-2026 Neel Desai / Golden Earth Software Consulting Inc.
// App context extraction — builds a full DTO snapshot of the Mendix app model
// ============================================================================
using AideLite.Models.DTOs;
using Mendix.StudioPro.ExtensionsAPI.Model;
using Mendix.StudioPro.ExtensionsAPI.Services;

namespace AideLite.ModelReaders;

public class AppContextExtractor
{
    private readonly IModel _model;
    private readonly DomainModelReader _domainModelReader;
    private readonly MicroflowReader _microflowReader;
    private readonly PageReader _pageReader;

    public AppContextExtractor(
        IModel model,
        IDomainModelService domainModelService,
        IMicroflowService microflowService,
        IUntypedModelAccessService untypedModelAccessService,
        ILogService logService)
    {
        _model = model;
        _domainModelReader = new DomainModelReader(model, domainModelService);
        _microflowReader = new MicroflowReader(model, microflowService, untypedModelAccessService, logService);
        _pageReader = new PageReader(model);
    }

    /// <summary>
    /// Extract a compact summary of the entire app for the system prompt.
    /// </summary>
    /// <param name="contextDepth">"full" for all modules, "module" for non-marketplace only</param>
    public AppContextDto ExtractAppContext(string contextDepth = "full")
    {
        var context = new AppContextDto { AppName = _model.Root.Name };
        var modules = _model.Root.GetModules();

        foreach (var module in modules)
        {
            // Marketplace modules add thousands of entities/microflows that overwhelm the prompt
            if (module.FromAppStore)
                continue;

            var moduleSummary = new ModuleSummaryDto
            {
                Name = module.Name,
                FromAppStore = module.FromAppStore,
                Entities = _domainModelReader.GetEntitySummaries(module),
                Associations = _domainModelReader.GetAssociationSummaries(module),
                Microflows = _microflowReader.GetMicroflowSummaries(module),
                Pages = _pageReader.GetPageSummaries(module),
                Enumerations = _domainModelReader.GetEnumerationSummaries(module)
            };

            context.Modules.Add(moduleSummary);
        }

        return context;
    }

    /// <summary>
    /// Extract a detailed app context with full entity details and enriched microflow summaries.
    /// More expensive than ExtractAppContext() but eliminates most read-only tool calls
    /// by front-loading data into the cached system prompt.
    /// </summary>
    public AppContextDto ExtractDetailedAppContext(string contextDepth = "full", int maxEntitiesForDetails = 200)
    {
        // Front-loads full entity details into the system prompt, eliminating most get_entity_details calls.
        // maxEntitiesForDetails caps this to avoid token explosion on very large apps.
        var context = new AppContextDto { AppName = _model.Root.Name };
        var modules = _model.Root.GetModules();
        var totalEntityCount = 0;

        foreach (var module in modules)
        {
            if (module.FromAppStore)
                continue;

            var entities = _domainModelReader.GetEntitySummaries(module);
            totalEntityCount += entities.Count;

            var moduleSummary = new ModuleSummaryDto
            {
                Name = module.Name,
                FromAppStore = module.FromAppStore,
                Entities = entities,
                Associations = _domainModelReader.GetAssociationSummaries(module),
                Pages = _pageReader.GetPageSummaries(module),
                Enumerations = _domainModelReader.GetEnumerationSummaries(module)
            };

            // Full entity details (only if total entity count is reasonable)
            if (totalEntityCount <= maxEntitiesForDetails)
                moduleSummary.EntityDetails = _domainModelReader.GetAllEntityDetails(module);

            // Enriched microflow summaries with params and activity types
            moduleSummary.Microflows = _microflowReader.GetEnrichedMicroflowSummaries(module);

            context.Modules.Add(moduleSummary);
        }

        return context;
    }

    /// <summary>
    /// Find a module by name.
    /// </summary>
    public Mendix.StudioPro.ExtensionsAPI.Model.Projects.IModule? FindModule(string moduleName)
    {
        return _model.Root.GetModules().FirstOrDefault(m =>
            string.Equals(m.Name, moduleName, StringComparison.OrdinalIgnoreCase));
    }
}
