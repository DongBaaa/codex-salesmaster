from pathlib import Path
import collections, datetime, hashlib, json
from decimal import Decimal

root=Path(r'C:\Users\beene\Documents\Codex\tradeplan-current-rental-client-20260926-v3')
evidence=Path(r'C:\Users\beene\Documents\Codex\tradeplan-rental-site-preservation-20260926')
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
target='DF819FA2-B216-4DA6-9080-E61C2C55CED2'
profile='FD895AD4-A338-4168-B4DC-D6A24A3F27E1'
observed={}
for name in ('service-run','service-fixed'):
    p=root/name
    proof=read(p/'verification.json');assert proof['passed'] and proof['sourceSnapshotUnchanged']
    a=read(p/'before-service-rows.private.json');b=read(p/'after-service-rows.private.json')
    before={t:{r['Id']:r for r in rows} for t,rows in a.items()}
    after={t:{r['Id']:r for r in rows} for t,rows in b.items()}
    for t in before:assert before[t].keys()==after[t].keys()
    pa,pb=before['RentalBillingProfiles'][profile],after['RentalBillingProfiles'][profile]
    assert Decimal(str(pa['MonthlyAmount']))==1287000 and Decimal(str(pb['MonthlyAmount']))==1463000
    assert pa['BillingRunsJson']==pb['BillingRunsJson'] and len(json.loads(pb['BillingRunsJson']))==2
    assert pa['LastBilledDate']==pb['LastBilledDate'] and pb['LastBilledDate'].startswith('2026-08-31')
    lines_before=json.loads(pa['BillingTemplateJson']);lines_after=json.loads(pb['BillingTemplateJson'])
    assert len(lines_before)==9 and len(lines_after)==10
    for line in lines_before:
        matches=[row for row in lines_after if row.get('IncludedAssetIds')==line.get('IncludedAssetIds')]
        assert len(matches)==1
        # The service may reorder lines and emit previously omitted nulls.
        assert all(line.get(k)==matches[0].get(k) for k in set(line)|set(matches[0]))
    related={k for k,r in before['RentalAssets'].items() if r['BillingProfileId']==profile and not r['IsDeleted']}
    assert len(related)==16 and target not in related
    business_changes={}
    metadata_changes=[]
    for k,r in before['RentalAssets'].items():
        if k==target:continue
        fields=[f for f in r if r[f]!=after['RentalAssets'][k][f]]
        business=[f for f in fields if f not in ('UpdatedAtUtc','IsDirty')]
        if business:business_changes[k]=business
        if fields:metadata_changes.append(k)
    if name=='service-run':
        assert set(business_changes)==related and all(v==['InstallSiteName'] for v in business_changes.values())
    else:
        assert not business_changes
        assert set(metadata_changes)==related
        for k,r in before['RentalAssetAssignmentHistories'].items():
            if r['AssetId']!=target:assert r==after['RentalAssetAssignmentHistories'][k]
        assert before['RentalManagementCompanies']==after['RentalManagementCompanies']
        for k,r in before['RentalBillingProfiles'].items():
            if k!=profile:assert r==after['RentalBillingProfiles'][k]
        a_hash=read(p/'before-service-hashes.json');b_hash=read(p/'after-service-hashes.json')
        changed=[t for t in a_hash if a_hash[t]!=b_hash[t]]
        assert set(changed)=={'RentalAssets','RentalBillingProfiles','RentalAssetAssignmentHistories'}
    observed[name]={'appSha256':proof['appSha256'],'otherAssetBusinessChanges':len(business_changes),'otherAssetMetadataChanges':len(metadata_changes),'preservedProfileRuns':2,'preservedExistingTemplateLines':9,'newMonthlyAmount':pb['MonthlyAmount'],'localServiceCallsPassed':len(proof['outcomes'])}
source=Path(r'C:\Users\beene\AppData\Local\거래플랜\data\거래플랜.db')
assert hashlib.sha256(source.read_bytes()).hexdigest()=='674e0022e2d7b3da5de18f499600e9f311d1da1406fa7dfd21c925574a7ead22'
result={'at':datetime.datetime.now().astimezone().isoformat(),'status':'passed-installation-site-preservation-local-service-replay','observed':observed,'originalPcDbUnchanged':True,'operationalWrites':0,'networkSyncInvoked':False,'authenticatedActorVerified':False,'apiAcceptanceReplayRollbackVerified':False,'remainingMetadataDirtyBehavior':16,'entireGoalComplete':False}
(evidence/'real-data-replay.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(result,ensure_ascii=False))
