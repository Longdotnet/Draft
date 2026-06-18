export type RevealRarity = "blue" | "purple" | "gold";

export function getRevealRarity(score: number): RevealRarity {
  if (score >= 3) return "gold";
  if (score >= 2) return "purple";
  return "blue";
}
