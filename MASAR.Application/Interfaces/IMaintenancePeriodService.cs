using Masar.Application.Common;
using Masar.Application.DTOs.Maintenance;

namespace Masar.Application.Interfaces;

public interface IMaintenancePeriodService
{
    Task<Result<MaintenancePeriodResponse>> CreateAsync(
        string createdByUserId, CreateMaintenancePeriodRequest request);
    Task<Result<MaintenancePeriodResponse>> UpdateAsync(
        int maintenancePeriodId, UpdateMaintenancePeriodRequest request);
    Task<Result<MaintenancePeriodResponse>> GetByIdAsync(int maintenancePeriodId);
    Task<Result<List<MaintenancePeriodResponse>>> GetByWorkspaceAsync(int workspaceId);
    Task<Result<bool>> DeleteAsync(int maintenancePeriodId);
}
