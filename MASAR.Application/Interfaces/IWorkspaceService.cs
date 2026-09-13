namespace Masar.Application.Interfaces;

using Masar.Application.DTOs.Workspaces;

public interface IWorkspaceService
{
    Task<WorkspaceResult> CreateAsync(CreateWorkspaceRequest request);
    Task<WorkspaceResponse?> GetByIdAsync(int id);
    Task<WorkspaceResult> UpdateAsync(int id, UpdateWorkspaceRequest request);
    Task<WorkspaceResult> ActivateAsync(int id);
    Task<WorkspaceResult> DeactivateAsync(int id);
}
