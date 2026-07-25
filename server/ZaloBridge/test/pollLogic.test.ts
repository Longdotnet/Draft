import assert from "node:assert/strict";
import test from "node:test";
import {
  normalizeMemberId,
  normalizePoll,
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
