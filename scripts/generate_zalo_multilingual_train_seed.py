#!/usr/bin/env python3
"""Produce synthetic, manually authored multi-language training examples.

No user messages are read. The separate 45-example evaluation corpus is not
consulted by this generator. This is a starter set, not evidence of general
natural-language understanding; held-out human paraphrases remain required.
"""
import argparse
import json
import random
from collections import Counter
from pathlib import Path

PHRASES = {
    "vi": {
        "together": [
            "xếp mình vào đội của {player} {when}",
            "mình đề nghị ghép cùng nhóm với {player} {when}",
            "{when} cho mình và {player} về một phe nhé",
            "xếp {player} chơi cùng mình {when}",
            "muốn bạn {player} làm đồng đội của mình {when}",
        ],
        "apart": [
            "hãy tách mình khỏi đội của {player} {when}",
            "xếp mình khác phe {player} {when}",
            "để mình ở đội đối diện với {player} {when}",
            "{when} tránh ghép mình và {player} chung một đội",
            "đừng để {player} và mình thành đồng đội {when}",
        ],
        "query": [
            "mình và {player} có thể ở cùng phía được chứ {when}?",
            "có được xếp mình cùng phe {player} {when} không?",
            "{when} liệu mình có cơ hội chung nhóm với {player} không?",
            "bạn cho biết có thể xếp mình với {player} cùng đội không?",
        ],
        "uncertain": [
            "nếu thuận tiện thì có thể thử cho mình cùng đội {player}",
            "mình cũng không phản đối việc chơi với {player}",
            "chắc là cũng được nếu {player} làm đồng đội",
            "mình chưa quyết chuyện chơi với {player}",
        ],
        "clear": [
            "bỏ ràng buộc đồng đội trước đây của mình và {player}",
            "xóa điều kiện cùng phe với {player} đã lưu",
            "gỡ thiết lập bắt buộc chung đội cùng {player}",
            "đừng giữ yêu cầu cũ phải ghép mình với {player}",
        ],
        "slot": [
            "cho mình với {player} luân phiên chơi một suất",
            "{player} và mình chia chung một chỗ đăng ký",
            "mình nhường nửa thời gian trong slot cho {player}",
            "hai người bọn mình thay ca trong một slot nhé",
        ],
        "general": [
            "ê bot còn đây không",
            "tối vui vẻ nhé mọi người",
            "bot ơi hôm nay ăn gì ngon",
            "chào cả nhóm, lâu rồi không gặp",
        ],
        "other": [
            "buổi chơi {when} bắt đầu lúc nào?",
            "sân của trận {when} ở đâu vậy?",
            "ngày {when} có bao nhiêu người đăng ký?",
        ],
        "when": ["hôm nay", "ngày mai", "tối thứ sáu"],
    },
    "en": {
        "together": [
            "assign me to the squad {player} belongs to {when}",
            "please pair me with {player} for the match {when}",
            "{when} arrange for {player} to play alongside me",
            "put {player} and me on one side for {when}",
            "I wish to partner up with {player} in the game {when}",
        ],
        "apart": [
            "assign me against {player} for {when}",
            "make {player} my opponent rather than my teammate {when}",
            "please separate me from {player} for the match {when}",
            "{when} ensure {player} plays on the opposite side",
            "don't pair me up with {player} {when}",
        ],
        "query": [
            "would it be possible to pair me with {player} {when}?",
            "am I allowed to partner {player} in the next game?",
            "is being paired with {player} possible {when}?",
            "can you tell me if I could share a team with {player}?",
        ],
        "uncertain": [
            "playing alongside {player} might be okay with me",
            "I'm not opposed to partnering with {player}",
            "perhaps {player} and I could play together if convenient",
            "I haven't decided whether to partner with {player}",
        ],
        "clear": [
            "delete the existing partner restriction involving {player}",
            "undo my earlier request to pair with {player}",
            "discard my previously saved teammate constraint with {player}",
            "remove the same-squad rule for me and {player}",
        ],
        "slot": [
            "let {player} and me take turns using one registration spot",
            "I want to split one seat with {player} instead of two",
            "{player} and I will alternate within one booking slot",
            "please register us as two people using one slot",
        ],
        "general": [
            "hey there bot how are things",
            "what a beautiful evening everybody",
            "good morning bot can you hear me",
            "how is everyone doing today",
        ],
        "other": [
            "when does the {when} match begin?",
            "what is the venue for the session {when}?",
            "how many players registered for {when}?",
        ],
        "when": ["today", "tomorrow", "on Friday"],
    },
    "ko": {
        "together": [
            "{when} {player} 님과 저를 한 조로 편성해 주세요",
            "이번 경기에 {player} 님 옆에서 뛰도록 배치해 주세요",
            "{player} 님의 팀에 저를 넣어 주세요 {when}",
            "{when} {player} 님이랑 저를 한 편으로 해 주세요",
            "{player} 님을 제 팀 동료로 정해 주세요 {when}",
        ],
        "apart": [
            "{when} {player} 님과 저를 서로 다른 조로 나눠 주세요",
            "{player} 님은 제 상대편으로 배정해 주세요",
            "{when} {player} 님하고 저는 같은 편에 넣지 말아 주세요",
            "{player} 님과 저를 반대편에서 뛰게 해 주세요 {when}",
            "{when} 제 팀에서 {player} 님을 분리해 주세요",
        ],
        "query": [
            "{when} {player} 님과 한 팀으로 뛸 수 있나요?",
            "제가 {player} 님과 같은 편에 배정될 수 있을까요?",
            "{player} 님하고 같이 경기해도 되는지 궁금해요",
            "{when} {player} 님을 동료로 선택할 수 있나요?",
        ],
        "uncertain": [
            "{player} 님과 같이 해도 저는 괜찮을 것 같아요",
            "가능하다면 {player} 님과 같은 조도 나쁘지 않겠네요",
            "{player} 님과 팀을 짤지 아직 결정하지 않았어요",
            "{player} 님이 동료여도 불만은 없어요",
        ],
        "clear": [
            "{player} 님과 동료로 배정하라는 기존 조건을 지워 주세요",
            "이전에 저장한 {player} 님과의 팀 요청을 철회할게요",
            "저와 {player} 님의 같은 팀 규칙을 해제해 주세요",
            "{player} 님과 묶어 달라는 요청을 삭제해 주세요",
        ],
        "slot": [
            "{player} 님과 한 자리를 번갈아 사용하고 싶어요",
            "하나의 참가 슬롯을 {player} 님과 나눠 쓰게 해 주세요",
            "{player} 님과 교대로 한 슬롯에서 경기할게요",
            "두 명이 등록 자리 하나만 공유할게요",
        ],
        "general": [
            "봇 안녕 오늘 기분 어때",
            "다들 좋은 저녁 보내세요",
            "봇 지금 대화할 수 있어요?",
            "오늘 날씨가 참 좋네요",
        ],
        "other": [
            "{when} 경기는 몇 시에 시작하나요?",
            "{when} 배구장은 어디예요?",
            "{when} 참가자가 몇 명인가요?",
        ],
        "when": ["오늘", "내일", "금요일에"],
    },
    "zh": {
        "together": [
            "{when}请安排我加入{player}的队伍",
            "这场球让我跟{player}成为队友{when}",
            "{when}把{player}分到我这边来",
            "请让{player}跟我站在同一方{when}",
            "我希望这次能跟{player}搭档{when}",
        ],
        "apart": [
            "{when}请让我和{player}分在不同队",
            "让{player}成为我的对手而非队友",
            "这场比赛别把我和{player}安排在一边",
            "{when}让我站在{player}对面那一队",
            "把我从{player}所在的队伍里分开{when}",
        ],
        "query": [
            "{when}我能和{player}一起组队吗？",
            "是否允许我与{player}做队友？",
            "请问这场球能让我和{player}搭档吗？",
            "{when}我可以申请加入{player}的队吗？",
        ],
        "uncertain": [
            "跟{player}搭档我也觉得可以",
            "我对和{player}一队没有意见",
            "我还没决定是否跟{player}一起打球",
            "要是方便的话与{player}合作也行",
        ],
        "clear": [
            "把之前和{player}组队的规定撤销掉",
            "删除我与{player}的队友偏好设置",
            "解除我必须和{player}同组的要求",
            "取消先前要求我与{player}搭档的记录",
        ],
        "slot": [
            "我跟{player}轮流占用一个名额",
            "请让我与{player}共享一个报名位置",
            "一个参加席位由我和{player}轮换使用",
            "我们两个人只用一个上场时段",
        ],
        "general": [
            "嗨机器人今天怎么样",
            "大家晚上好玩得开心",
            "机器人你在这里吗",
            "这星期的天气真不错",
        ],
        "other": [
            "{when}那场比赛几点开始？",
            "{when}的球场在哪儿？",
            "{when}有多少人报名？",
        ],
        "when": ["今天", "明天", "星期五"],
    },
    "fr": {
        "together": [
            "place-moi dans l'équipe de {player} {when}",
            "je souhaite faire équipe avec {player} pour le match {when}",
            "{when} associe {player} et moi comme partenaires",
            "fais jouer {player} de mon côté {when}",
            "je préfère avoir {player} comme coéquipier {when}",
        ],
        "apart": [
            "mets-moi dans le camp opposé à {player} {when}",
            "{when} sépare-moi de l'équipe de {player}",
            "je veux affronter {player} plutôt que jouer avec lui",
            "évite de nous mettre ensemble avec {player} {when}",
            "fais de {player} mon adversaire lors du match {when}",
        ],
        "query": [
            "serait-il possible de me placer avec {player} {when} ?",
            "ai-je le droit de faire équipe avec {player} ?",
            "pourrais-je rejoindre le groupe de {player} {when} ?",
            "est-ce possible de jouer du côté de {player} ?",
        ],
        "uncertain": [
            "jouer avec {player} me conviendrait aussi",
            "je n'ai rien contre le fait d'être partenaire de {player}",
            "je n'ai pas encore décidé pour l'équipe de {player}",
            "pourquoi pas jouer avec {player} si ça arrange tout le monde",
        ],
        "clear": [
            "annule mon ancienne consigne de jouer avec {player}",
            "efface ma préférence de coéquipier concernant {player}",
            "retire la règle exigeant que {player} et moi soyons ensemble",
            "supprime notre association enregistrée avec {player}",
        ],
        "slot": [
            "je veux alterner une seule place avec {player}",
            "partage un créneau d'inscription entre {player} et moi",
            "nous allons utiliser un seul slot à tour de rôle",
            "{player} et moi occupons une place en alternance",
        ],
        "general": [
            "salut le bot comment vas-tu",
            "bonne soirée à tous les amis",
            "bonjour le bot tu es disponible",
            "quel beau temps pour jouer dehors",
        ],
        "other": [
            "à quelle heure commence la rencontre {when} ?",
            "où se trouve le terrain pour {when} ?",
            "combien de joueurs sont inscrits {when} ?",
        ],
        "when": ["aujourd'hui", "demain", "vendredi"],
    },
}

LABELS = {
    "together": ("TeamPreference", "REQUEST"),
    "apart": ("TeamPreference", "REQUEST"),
    "query": ("TeamPreference", "QUESTION"),
    "uncertain": ("TeamPreference", "UNCERTAIN"),
    "clear": ("TeamPreference", "REQUEST"),
    "slot": ("ShareSlot", "REQUEST"),
    "general": ("GeneralChat", "QUESTION"),
    "other": ("Other", "QUESTION"),
}

def main():
    p=argparse.ArgumentParser()
    p.add_argument("--output",type=Path,required=True)
    args=p.parse_args()
    examples=[]
    for lang, groups in PHRASES.items():
        for category, (intent, speech_act) in LABELS.items():
            for index,template in enumerate(groups[category]):
                for sample, (name, when) in enumerate(zip(["@Minh", "@Jean", "@Jimin"],groups["when"])):
                    examples.append({
                        "id":f"{lang}-{category}-{index:02d}-{sample}",
                        "language":lang,"text":template.format(player=name,when=when),
                        "intent":intent,"speechAct":speech_act,
                        "origin":"synthetic-manually-authored-template"
                    })
    random.Random(409).shuffle(examples)
    args.output.parent.mkdir(parents=True,exist_ok=True)
    args.output.write_text("".join(json.dumps(x,ensure_ascii=False)+"\n" for x in examples),encoding="utf-8")
    print("Written",len(examples),"synthetic training examples to",args.output)
    print(Counter(x["language"] for x in examples))

if __name__=="__main__":
    main()
