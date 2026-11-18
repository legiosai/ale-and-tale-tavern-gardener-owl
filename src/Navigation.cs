using UnityEngine;
using UnityEngine.AI;

namespace GardenHarvesterOwl;

internal static class Navigation
{
    internal static bool AgentReady(NavMeshAgent agent) =>
        agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh;

    internal static NavMeshQueryFilter Filter(NavMeshAgent agent) => new()
    {
        agentTypeID = agent.agentTypeID,
        areaMask = agent.areaMask,
    };

    internal static bool TryLanding(HelperCleaner owl, out Vector3 point)
    {
        point = default;
        if (owl.landingPos == null)
            return false;
        Vector3 landing = owl.landingPos.position;
        if (!NavMesh.SamplePosition(landing, out NavMeshHit hit, 1.4f, Filter(owl.agent))
            || Mathf.Abs(hit.position.y - landing.y) > 1.1f)
            return false;
        point = hit.position;
        return true;
    }

    internal static bool TryBedPath(HelperCleaner owl, Vector3 start, GardenBed bed, NavMeshPath path)
    {
        var filter = Filter(owl.agent);
        Vector3 center = bed.transform.position;
        Vector3 near = start - center;
        near.y = 0f;
        near = near.sqrMagnitude < 0.01f ? Vector3.forward : near.normalized;

        // Aim beside the bed first, where the owl can stand next to the crop.
        for (int step = 0; step < 8; step++)
        {
            int angle = step % 2 == 0 ? step / 2 : -(step + 1) / 2;
            Vector3 edge = center + Quaternion.Euler(0f, angle * 45f, 0f) * near;
            if (!NavMesh.SamplePosition(edge, out NavMeshHit hit, 0.75f, filter) || !IsBeside(owl, hit.position, center))
                continue;
            if (TryPath(start, hit.position, filter, path))
                return true;
        }
        return false;
    }

    internal static bool TryPath(Vector3 start, Vector3 target, NavMeshQueryFilter filter, NavMeshPath path) =>
        NavMesh.CalculatePath(start, target, filter, path) && path.status == NavMeshPathStatus.PathComplete;

    internal static bool IsBeside(HelperCleaner owl, Vector3 position, Vector3 bed) =>
        Mathf.Abs(position.y - bed.y) <= 0.9f
        && HorizontalSqrDistance(position, bed) <= Mathf.Pow(owl.targetDetectDistance, 2f);

    internal static float HorizontalSqrDistance(Vector3 a, Vector3 b)
    {
        float x = a.x - b.x;
        float z = a.z - b.z;
        return x * x + z * z;
    }
}
