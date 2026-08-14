import type { BridgeMention, BridgeMessageQuote } from "./contracts.js";
import { normalizeMemberId } from "./pollLogic.js";

export type AddressedMessage = {
  mentions: BridgeMention[];
  mentionedBot: boolean;
  addressedByReply: boolean;
};

/**
 * Zalo users often tap Reply and answer the bot with a short value such as
 * "T6" or "xác nhận" instead of typing @bot again. Treat a quote owned by the
 * bot as an explicit conversational address, while preserving real mentions.
 */
export function resolveBotAddressing(
  mentions: BridgeMention[],
  quote: BridgeMessageQuote | null | undefined,
  botIdValue: string,
): AddressedMessage {
  const botId = normalizeMemberId(botIdValue);
  const hasRealMention = mentions.some((mention) => normalizeMemberId(mention.uid) === botId);
  const addressedByReply = Boolean(botId && normalizeMemberId(quote?.senderId ?? "") === botId);

  if (!addressedByReply || hasRealMention) {
    return {
      mentions,
      mentionedBot: hasRealMention || addressedByReply,
      addressedByReply,
    };
  }

  // The API currently verifies bot addressing by UID in Mentions. A synthetic
  // one-character marker preserves that invariant without pretending Zalo sent
  // a textual @mention. It is transport metadata only and is never rendered.
  return {
    mentions: [...mentions, { uid: botId, pos: 0, len: 1 }],
    mentionedBot: true,
    addressedByReply: true,
  };
}
