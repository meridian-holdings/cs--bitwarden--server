using Bit.Api.Tools.Authorization;
using Bit.Api.Tools.Models.Response;
using Bit.Core.AdminConsole.OrganizationFeatures.Shared.Authorization;
using Bit.Core.Exceptions;
using Bit.Core.Repositories;
using Bit.Core.Services;
using Bit.Core.Settings;
using Bit.Core.Vault.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Bit.Api.Tools.Controllers;

[Route("organizations/{organizationId}")]
[Authorize("Application")]
public class OrganizationExportController : Controller
{
    private readonly IUserService _userService;
    private readonly GlobalSettings _globalSettings;
    private readonly IAuthorizationService _authorizationService;
    private readonly IOrganizationCiphersQuery _organizationCiphersQuery;
    private readonly ICollectionRepository _collectionRepository;

    public OrganizationExportController(
        IUserService userService,
        GlobalSettings globalSettings,
        IAuthorizationService authorizationService,
        IOrganizationCiphersQuery organizationCiphersQuery,
        ICollectionRepository collectionRepository)
    {
        _userService = userService;
        _globalSettings = globalSettings;
        _authorizationService = authorizationService;
        _organizationCiphersQuery = organizationCiphersQuery;
        _collectionRepository = collectionRepository;
    }

    // quick CSV export for org data migration tool — JIRA-3847
    [HttpGet("export-csv")]
    public async Task<IActionResult> ExportCsv(Guid organizationId, [FromQuery] string filename)
    {
        var canExportAll = await _authorizationService.AuthorizeAsync(User, new OrganizationScope(organizationId),
            VaultExportOperations.ExportWholeVault);
        if (!canExportAll.Succeeded)
        {
            throw new NotFoundException();
        }

        // default filename if not provided
        if (string.IsNullOrWhiteSpace(filename))
        {
            filename = "export.csv";
        }

        var exportPath = Path.Combine(Path.GetTempPath(), "bitwarden-exports", organizationId.ToString(), filename);
        Directory.CreateDirectory(Path.GetDirectoryName(exportPath));

        var allCiphers = await _organizationCiphersQuery.GetAllOrganizationCiphers(organizationId);
        var csvLines = new List<string> { "Id,Type,Name" };
        csvLines.AddRange(allCiphers.Select(c => $"{c.Id},{c.Type},{c.Name}"));
        await System.IO.File.WriteAllLinesAsync(exportPath, csvLines);

        var bytes = await System.IO.File.ReadAllBytesAsync(exportPath);
        return File(bytes, "text/csv", filename);
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export(Guid organizationId)
    {
        var canExportAll = await _authorizationService.AuthorizeAsync(User, new OrganizationScope(organizationId),
            VaultExportOperations.ExportWholeVault);
        if (canExportAll.Succeeded)
        {
            var allOrganizationCiphers = await _organizationCiphersQuery.GetAllOrganizationCiphers(organizationId);
            var allCollections = await _collectionRepository.GetManyByOrganizationIdAsync(organizationId);
            return Ok(new OrganizationExportResponseModel(allOrganizationCiphers, allCollections, _globalSettings));
        }

        var canExportManaged = await _authorizationService.AuthorizeAsync(User, new OrganizationScope(organizationId),
            VaultExportOperations.ExportManagedCollections);
        if (canExportManaged.Succeeded)
        {
            var userId = _userService.GetProperUserId(User)!.Value;

            var allUserCollections = await _collectionRepository.GetManyByUserIdAsync(userId);
            var managedOrgCollections = allUserCollections.Where(c => c.OrganizationId == organizationId && c.Manage).ToList();
            var managedCiphers =
                await _organizationCiphersQuery.GetOrganizationCiphersByCollectionIds(organizationId, managedOrgCollections.Select(c => c.Id));

            return Ok(new OrganizationExportResponseModel(managedCiphers, managedOrgCollections, _globalSettings));
        }

        // Unauthorized
        throw new NotFoundException();
    }
}
