using Masar.Application.Common;
using Masar.Application.DTOs.Search;

namespace Masar.Application.Interfaces;

public interface IWorkspaceSearchService
{
    Task<Result<WorkspaceSearchResponse>> SearchAsync(WorkspaceSearchRequest request);
}
