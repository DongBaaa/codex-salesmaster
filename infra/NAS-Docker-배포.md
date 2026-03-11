# 코덱스 레거시 판매관리 NAS Docker 배포

이 구성은 `NAS에서 Docker로 서버 1개를 실행`하고, `다른 PC의 레거시 판매관리 클라이언트가 모두 그 서버에 접속`하는 용도입니다.

## 권장 구조

- NAS Docker: `salesmaster-api`
- NAS Docker: `salesmaster-postgres`
- NAS 저장 경로: `infra/data/postgres`
- 각 PC 클라이언트: `http://192.0.2.10:18080` 으로 접속

중요:

- 데이터베이스 파일을 여러 PC가 직접 공유 폴더로 열면 안 됩니다.
- 반드시 `서버 컨테이너 1개만` 데이터베이스에 접근하고, 각 PC는 API로만 붙어야 합니다.

## NAS 폴더 예시

```text
\\192.0.2.10\Codex Server\레거시 판매관리\
  infra\
    docker-compose.yml
    .env
    data\
      postgres\
```

`docker-compose.yml` 의 `./data/postgres` 는 compose 파일 기준 상대 경로입니다. NAS 내부 볼륨에 그대로 저장됩니다.

## 1. 환경변수 준비

`infra\.env.example` 을 복사해서 `infra\.env` 로 만든 뒤 최소한 아래 값은 바꾸세요.

```env
POSTGRES_PASSWORD=강한비밀번호
JWT_SIGNING_KEY=충분히긴랜덤키
```

기본 포트는 `18080` 입니다.

## 2. 서버 실행

`infra` 폴더에서 실행:

```bash
docker compose up -d --build
```

정상 동작 확인:

```bash
docker compose ps
docker compose logs -f api
```

브라우저 또는 다른 PC에서 아래 주소가 열리면 서버가 보이는 상태입니다.

```text
http://192.0.2.10:18080
```

## 3. 각 PC 클라이언트 연결

클라이언트 `appsettings.json` 의 `Api.BaseUrl` 을 아래처럼 맞춥니다.

```json
{
  "Api": {
    "BaseUrl": "http://192.0.2.10:18080"
  }
}
```

이미 배포된 앱 폴더가 있으면 `배포\Set-ApiBaseUrl.ps1` 스크립트를 써도 됩니다.

예시:

```powershell
powershell -ExecutionPolicy Bypass -File .\Set-ApiBaseUrl.ps1 -BaseUrl http://192.0.2.10:18080
```

## 4. 다른 PC에서 꼭 확인할 항목

- `192.0.2.10:18080` 방화벽 허용
- NAS Docker가 같은 사설망에 연결되어 있는지 확인
- 클라이언트 PC에서 `http://192.0.2.10:18080` 접속 가능 여부 확인

## 5. 운영 팁

- PostgreSQL 데이터는 `infra/data/postgres` 만 백업하면 됩니다.
- 서버 로그는 `docker compose logs api` 로 확인합니다.
- JWT 서명 키를 바꾸면 기존 로그인 토큰은 다시 로그인해야 합니다.
- 여러 PC가 동시에 써도 괜찮지만, 반드시 모두 같은 NAS API 주소를 써야 합니다.
