import assert from "node:assert/strict";
import test from "node:test";
import {
  isGroupMemberDirectoryComplete,
  normalizeMemberId,
  normalizePoll,
  shouldLoadGroupLinkDirectory,
  shouldPropagateGroupMemberDirectoryFailure,
  shouldIncludeOwnAccount,
  uniqueVoterIds,
} from "../src/pollLogic.js";

test("deduplicates voters across selected multi-choice options", () => {
  const poll = normalizePoll({
    poll_id: "90071992547409931234",
    question: "Lịch chơi",
    options: [
      { option_id: 1, content: "T4", votes: 2, voters: ["u1", "u2"] },
      { option_id: 2, content: "T6", votes: 2, voters: ["u2", "u3_0"] },
    ],
  });

  assert.equal(poll.id, "90071992547409931234");
  assert.deepEqual(uniqueVoterIds(poll.options, ["1", "2"]), ["u1", "u2", "u3"]);
});

test("normalizes member profile version suffix without coercing IDs to numbers", () => {
  assert.equal(normalizeMemberId("12345678901234567890_0"), "12345678901234567890");
});

test("fills only the exact group total gap with the authenticated account", () => {
  assert.equal(shouldIncludeOwnAccount(100, 99, "bot-uid", false), true);
  assert.equal(shouldIncludeOwnAccount(100, 98, "bot-uid", false), false);
  assert.equal(shouldIncludeOwnAccount(100, 99, "bot-uid", true), false);
  assert.equal(shouldIncludeOwnAccount(0, 0, "bot-uid", false), false);
});

test("uses the invite-link directory only when group info does not provide enough member UIDs", () => {
  assert.equal(shouldLoadGroupLinkDirectory(false, 100, 100), true);
  assert.equal(shouldLoadGroupLinkDirectory(true, 100, 0), true);
  assert.equal(shouldLoadGroupLinkDirectory(true, 100, 99), true);
  assert.equal(shouldLoadGroupLinkDirectory(true, 100, 100), false);
  assert.equal(shouldLoadGroupLinkDirectory(true, 80, 100), false);
  assert.equal(shouldLoadGroupLinkDirectory(true, 0, 0), false);
});

test("never marks a missing provider directory complete just because it is empty", () => {
  assert.equal(isGroupMemberDirectoryComplete(false, false, 0, 0, 0), false);
  assert.equal(isGroupMemberDirectoryComplete(true, false, 0, 0, 0), true);
  assert.equal(isGroupMemberDirectoryComplete(true, false, 10, 9, 0), false);
  assert.equal(isGroupMemberDirectoryComplete(true, false, 10, 10, 1), true);
  assert.equal(isGroupMemberDirectoryComplete(false, true, 0, 0, 0), true);
});

test("only retries member-directory fallback when the primary lookup itself failed", () => {
  assert.equal(shouldPropagateGroupMemberDirectoryFailure(true, false, 0), true);
  assert.equal(shouldPropagateGroupMemberDirectoryFailure(false, false, 0), false);
  assert.equal(shouldPropagateGroupMemberDirectoryFailure(true, true, 0), false);
  assert.equal(shouldPropagateGroupMemberDirectoryFailure(true, false, 1), false);
});
