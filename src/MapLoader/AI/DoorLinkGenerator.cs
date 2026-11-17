using System.Collections.Generic;
using EFT.Interactive;
using UnityEngine;

namespace KennerZombies.MapLoader.AI
{
    // Creates a NavMeshDoorLink for every operable Door, which is how bots find and open doors.
    // Without links a closed door carves nothing out of the navmesh and bots walk through it.
    //
    // Ported from an earlier DoorScanner. Conventions measured on Factory's baked links:
    //  - Open1 == Close1 == the hinge (door.transform.position)
    //  - Open2 = leaf end in the shut pose; Close2_Normal = leaf end in the open pose
    //  - Close2_Breach = open pose swung back ~16°
    //  - carvers must exist (BotDoorsController.InitCarveStates dereferences them), hence TryCreateCrave
    //  - BotDoorsController.RefreshData pairs links to doors by DoorId later in BotsController.Init
    internal static class DoorLinkGenerator
    {
        private const float BreachBackswingDeg = 16f;

        public static List<NavMeshDoorLink> Generate()
        {
            var links = new List<NavMeshDoorLink>();
            var root = new GameObject("SDK_GeneratedDoorLinks");
            int id = 1;
            foreach (var door in Object.FindObjectsOfType<Door>())
            {
                // no links on doors that start locked: bots can't open them
                if (door == null || !door.Operatable || door.DoorState == EDoorState.Locked || string.IsNullOrEmpty(door.Id))
                    continue;

                var t = door.transform;
                var hinge = t.position;
                // the swing axis resolves against the parent, the leaf direction against the door
                var axis = WorldInteractiveObject.GetRotationAxis(door.DoorAxis, t.parent);
                var leafNow = WorldInteractiveObject.GetRotationAxis(door.DoorForward, t);
                if (axis.sqrMagnitude < 0.5f || leafNow.sqrMagnitude < 0.5f)
                    continue;

                float curAngle = door.GetAngle(door.DoorState);
                float width = LeafWidth(door, leafNow);
                float swingSign = Mathf.Sign(door.OpenAngle - door.CloseAngle);
                var shutDir = LeafDirAt(door.GetAngle(EDoorState.Shut), curAngle, axis, leafNow);
                var openDir = LeafDirAt(door.OpenAngle, curAngle, axis, leafNow);
                var breachDir = LeafDirAt(door.OpenAngle - swingSign * BreachBackswingDeg, curAngle, axis, leafNow);
                if (shutDir == Vector3.zero || openDir == Vector3.zero)
                    continue;

                var go = new GameObject($"DoorLink_{id}");
                go.transform.SetParent(root.transform);
                go.transform.position = hinge;
                var link = go.AddComponent<NavMeshDoorLink>();
                link.Id = id++;
                link.DoorId = door.Id;
                link.Open1 = hinge;
                link.Close1 = hinge;
                link.Open2 = hinge + shutDir * width;
                link.Close2_Normal = hinge + openDir * width;
                link.Close2_Breach = hinge + breachDir * width;
                // retail Factory links use false: ShallInteract() then always allows opening
                link.ShallTryInteract = false;
                // Awake ran during AddComponent with zeroed fields; recompute the midpoints the
                // door opener measures distance to
                link.MidOpen = (link.Open1 + link.Open2) / 2f;
                link.MidClose = (link.Close1 + link.Close2_Normal) / 2f;
                try
                {
                    link.TryCreateCrave();
                    links.Add(link);
                }
                catch
                {
                    // a link without carvers breaks BotDoorsController; drop it
                    Object.Destroy(go);
                }
            }
            return links;
        }

        private static Vector3 LeafDirAt(float angle, float curAngle, Vector3 axis, Vector3 leafNow)
        {
            var d = Quaternion.AngleAxis(angle - curAngle, axis) * leafNow;
            d.y = 0f;
            return d.sqrMagnitude > 0.0001f ? d.normalized : Vector3.zero;
        }

        // leaf length from the door's colliders projected on the leaf direction
        private static float LeafWidth(Door door, Vector3 leafDir)
        {
            float best = 0f;
            foreach (var col in door.GetComponentsInChildren<Collider>())
            {
                if (col == null || col.isTrigger)
                    continue;
                var e = col.bounds.extents;
                float proj = Mathf.Abs(e.x * leafDir.x) + Mathf.Abs(e.y * leafDir.y) + Mathf.Abs(e.z * leafDir.z);
                best = Mathf.Max(best, proj * 2f);
            }
            return best > 0.3f ? Mathf.Clamp(best, 0.5f, 2.5f) : 1.08f;
        }
    }
}
