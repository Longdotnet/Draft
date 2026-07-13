import assert from "node:assert/strict";
import test from "node:test";

test("returns owner and deputy IDs for authorization", async () => {
  process.env.ZALO_BRIDGE_MOCK = "true";
  const [{ getGroupRoles }, { mockCredentials }] = await Promise.all([
    import("../src/zaloGateway.js"),
    import("../src/mockData.js"),
  ]);

  const roles = await getGroupRoles(mockCredentials, "group-ute");

  assert.equal(roles.groupId, "group-ute");
  assert.equal(roles.creatorId, "zalo-1");
  assert.deepEqual(roles.adminIds, ["zalo-2", "zalo-3"]);
});
