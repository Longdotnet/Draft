from pathlib import Path
p=Path('server/VolleyDraft.Api/Services/ZaloOverbookObservation.cs')
s=p.read_text(encoding='utf-8')
old='''        state.FriendlyMessages,\n        state.SeriousMessages,\n        state.StrictMessages,\n        state.OrderConfidence,'''
new='''        state.FriendlyMessages,\n        state.SeriousMessages,\n        state.StrictMessages,\n        ZaloOverbookMessageCatalog.GetUiBanks(state.ReminderMessageBanks),\n        state.OrderConfidence,'''
if old not in s: raise SystemExit('status constructor anchor missing')
p.write_text(s.replace(old,new,1),encoding='utf-8')
print('fixed')