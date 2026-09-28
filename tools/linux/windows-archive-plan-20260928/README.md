# 2026-09-28 지정 Windows 자료 SSD 보관·정리 실행기

이 도구는 오전9시 거래플랜 설치·배포 공간 확보를 위한 고정 명세 전용이다. **현재 사용자의 새 지정 범위 정리 승인은 미수신**이다. 코드나 실행 인수에 해시가 있다는 것은 사용자 승인을 대신하지 않는다. 승인 전에는 `preflight`만 실행한다.

## 고정 범위

- 명세: `C:\Users\beene\Documents\Codex\disk-capacity-followup-20260928\reviewed-archive-cleanup-plan.json`
- SHA-256: `96a64537048478645bcb867816ef514a7ee6e602854472912e41e054d19ce328`
- 9개 루트/10,122파일, 논리 크기32.73GiB. 희소 파일을 포함하므로 예상 실제 회수량과 다르다.
- 전체를 `/mnt/ssd2/archives/tradeplan/windows-20260928`에 gzip tar로 보관한다. 보관본 합계 최대12GiB, SSD 잔여 최소24GiB. 승인 후 부족하면 원본을 보존한 채 중단한다.
- 실제 삭제는 `deleteAfterVerifiedArchive=true`인6,612파일만. 소스/DB/JSON/로그 등3,510파일 및 디렉터리는 PC에 남긴다.
- 원본 업무DB·Git index·현재 앱/API DLL 해시를 전후 확인한다. NAS/Hyper-V/워크플랜/rt는 변경하지 않는다.

## 구현과 제한

`execute_plan.py`는 명세 해시·절대 경로·전체 파일 목록·reparse/Git 경계·mtime/크기/fileId/하드링크·전수SHA-256·사용 중 프로세스를 확인한다. Windows 파일과 부모 디렉터리를 핸들로 잠가 검사 중 쓰기/이름 변경을 막는다. 실제 삭제도 마지막으로 해시를 확인한 동일 파일 핸들의 삭제 표시로 수행하며, 의도/완료를 파일별 저널에 fsync한다.

업로드는 파일을 하나씩 독점 읽기로 잡고 전송한다. Windows에 큰 중간 tar를 만들지 않는다. SSD에서는 정확한 `/dev/sdb1` ext4 마운트, 경로/동시 실행 잠금, 합계 크기/잔여 공간을 검사한다. 기존 보관 시도는 덮어쓰지 않는다.

`receive_archive.py`는 기존 `verify_archive.py`로 모든 보관 파일의 경로/크기/해시를 검증하고, 각 루트의 작은 파일 하나를 고정된 샘플 경로에 실제로 풀어 다시 비교한다. 업로드 전체가 검증된 뒤 별도 `delete` 단계에서 원격 전체 검증을 다시 수행한다. 검증 실패 시 PC 삭제에 진입하지 않는다.

실패한 업로드/부분 삭제는 자동 재시작·자동 정리하지 않는다. 남은 파일·보관본·저널을 보존하고 원인을 확인해야 한다. 원복은 보관본에서 저널에 기록된 파일을 원래 경로에 복원하고 명세 해시로 확인하는 방식이다. 전체 디렉터리를 덮어쓰지 않는다.

## 실행 순서

현재는 첫 줄만 실행 가능하다. 뒤의 두 단계는 명세에 대한 사용자 승인 확인 후에만 실행한다. cwd는 `D:\거래플랜`이다.

```powershell
& 'C:\Users\beene\AppData\Local\Programs\Python\Python313\python.exe' -X utf8 'tools\linux\windows-archive-plan-20260928\execute_plan.py' preflight

# 명시적 사용자 승인 확인 후:
& 'C:\Users\beene\AppData\Local\Programs\Python\Python313\python.exe' -X utf8 'tools\linux\windows-archive-plan-20260928\execute_plan.py' upload --approved-plan-sha256 96a64537048478645bcb867816ef514a7ee6e602854472912e41e054d19ce328

# upload 종료0 및 execution/all-verified.json의 전수 검증 확인 후:
& 'C:\Users\beene\AppData\Local\Programs\Python\Python313\python.exe' -X utf8 'tools\linux\windows-archive-plan-20260928\execute_plan.py' delete --approved-plan-sha256 96a64537048478645bcb867816ef514a7ee6e602854472912e41e054d19ce328
```

도구가 기다리는 동안에는 반환된 실행 세션을 계속 관찰한다. 출력이 잠시 없다는 이유로 새 업로드나 삭제를 시작하지 않는다.

## 현재 검증

- 읽기 전용 실제 preflight: **10,122파일 전부 통과**, 업로드0/대상 삭제0.
- 실제 SSD 읽기: `/dev/sdb1` ext4, 잔여41,443,119,104바이트. `archives` 상위 폴더는 아직 없으므로 승인 후 안전 검사와 함께 생성한다.
- 합성 자료 검사 **25개 통과**(새16개+기존보관검증9개). 실제 Windows 독점 핸들/부모 이름 변경 차단/하드링크 거절/핸들 삭제, 대상 외 보존, 저널, 원격 검증 실패 시 삭제 미진입, 용량/예비공간, 보관본 전수 대조/실제 샘플 추출을 포함한다.
- Linux mount/flock은 Windows 검사에서 모의 대체했으므로 실제 Linux 동시 실행 검증 완료라고 확대하지 않는다. 실행 시 실제 receiver에서 필수 확인한다.
- 증거: `C:\Users\beene\Documents\Codex\disk-capacity-followup-20260928\executor-preflight.log`, `executor-tests-integration.log`, `executor-remote-readonly.log`.

## 정리 후 배포 순서

최신 계획은 `tools/linux/LIVE_DEADLINE_1746_20260928.md`와 deadline 증거의 checkpoint를 따른다. 새 설치 패키지는 **D의 별도 출력 경로**를 사용해 C의8GiB 설치 검증 여유를 유지하는 방향으로 산정한다. 1.1.745는 최신 수정이 빠진 과거 후보라 배포하지 않는다. 새 DB 분리 전환과 미확정 업무 금액 수정은 이번 긴급 배포에서 제외한다. 실제 공간 확보·공식 시험·새 패키지·원복 검증·live 반영은 아직 완료되지 않았다.
