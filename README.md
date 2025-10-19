# FSDBSlim

FSDBSlim é uma API HTTP leve escrita em .NET 9 para armazenar, versionar e recuperar arquivos em PostgreSQL (campo blob em bytea). O projeto foi pensado para rodar como um binário único em dispositivos limitados — por exemplo, Raspberry Pi 3 — sem necessidade de Docker.

Este README reúne as informações essenciais para uso, desenvolvimento, deploy e exemplos práticos.

Índice
- Visão geral
- Vantagens
- Requisitos
- Estrutura do banco de dados (exemplo)
- Variáveis de ambiente / Configuração
- Como compilar e publicar (Raspberry Pi 3 / linux-arm)
- Executando localmente
- API — Endpoints e exemplos de uso (curl)
- Como rodar como serviço systemd (exemplo)
- Boas práticas e segurança
- Contribuindo
- Licença

Visão geral
FSDBSlim fornece:
- Upload de arquivos (cria uma nova versão quando o mesmo nome é reenviado).
- Download de uma versão específica ou da versão mais recente.
- Listagem de versões de um arquivo.
- Remoção lógica / física de versões (dependendo da implementação).
- Metadados associados (JSON).
- Armazenamento em PostgreSQL usando bytea.

Vantagens
- Leve e simples: API HTTP mínima, focada em arquivos.
- Sem Docker: pode ser executado como um binário único, ideal para embarcados (Raspberry Pi).
- Versionamento integrado: cada upload cria/atualiza versões do arquivo.
- Uso de PostgreSQL: confiabilidade, backups e escalabilidade.
- Fácil integração via HTTP (curl, scripts, outras aplicações).

Requisitos
- .NET 9 SDK (para compilar) — ao desenvolver.
- Para execução como binário publicado: runtime linux-arm (Raspberry Pi 3).
- PostgreSQL 12+ (ou compatível).
- Espaço em disco e memória adequados conforme volume de arquivos.

Estrutura do banco de dados (exemplo)
Abaixo um exemplo de migration / SQL para criar a tabela principal. Ajuste conforme necessidade.

```sql
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";

CREATE TABLE fsd_files (
  id UUID DEFAULT uuid_generate_v4() PRIMARY KEY,
  name TEXT NOT NULL,
  version INTEGER NOT NULL DEFAULT 1,
  content BYTEA NOT NULL,
  content_size BIGINT NOT NULL,
  checksum TEXT,
  metadata JSONB,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  created_by TEXT
);

CREATE INDEX idx_fsd_files_name ON fsd_files(name);
```

Notas:
- Recomenda-se adicionar índices e políticas de retenção/limpeza conforme a necessidade.
- Pode-se ter uma tabela separada de “manifests” caso queira manter um ponteiro para a versão atual.

Variáveis de ambiente / Configuração
A aplicação deve ser configurada via environment variables (exemplo):

- DATABASE_URL (ex.: "Host=localhost;Port=5432;Database=fsdb;Username=user;Password=pass")
- ASPNETCORE_URLS (ex.: "http://*:5000")
- PORT (opcional, caso a app leia separadamente)
- MAX_FILE_SIZE (opcional — tamanho máximo em bytes)
- LOG_LEVEL (ex.: Information / Debug)

Como compilar e publicar (Raspberry Pi 3 / linux-arm)
Para gerar um único binário self-contained para Raspberry Pi 3 (ARM32), rode:

```bash
# No computador de desenvolvimento com .NET 9 SDK instalado
cd /path/to/FSDBSlim

# Publicar como single-file e self-contained para linux-arm (Raspberry Pi 3)
dotnet publish -c Release -r linux-arm \
  -p:PublishSingleFile=true \
  -p:PublishTrimmed=true \
  --self-contained true \
  -o ./publish
```

Isto produzirá um executável na pasta `publish/` pronto para ser copiado para o Raspberry Pi 3.

Observações:
- Se o seu Pi for 64-bit (arm64), use `-r linux-arm64`.
- Teste o binário no destino antes de automatizar deploys.
- PublishTrimmed pode reduzir o tamanho, mas teste para garantir que nada foi removido que você precisa (por reflexão, etc).

Executando localmente
1. Configure o PostgreSQL e exporte DATABASE_URL:
```bash
export DATABASE_URL="Host=localhost;Port=5432;Database=fsdb;Username=fsdb;Password=secret"
export ASPNETCORE_URLS="http://*:5000"
```

2. Execute a app durante desenvolvimento:
```bash
dotnet run --project src/FSDBSlim/FSDBSlim.csproj
```

3. Ou execute o binário publicado:
```bash
./publish/FSDBSlim
```

API — Endpoints e exemplos de uso
Abaixo está um conjunto de endpoints esperados (ajuste aos nomes reais do projeto). Exemplos usam curl.

1) Upload / criar nova versão
- POST /files
- Form-data: file (arquivo), name (nome do arquivo opcional), metadata (JSON opcional)

Exemplo:
```bash
curl -v -X POST "http://localhost:5000/files" \
  -F "file=@./document.pdf" \
  -F "name=document.pdf" \
  -F 'metadata={"owner":"user1","tags":["relatorio","2025"]}'
```

Resposta (exemplo):
```json
{
  "id": "3f1c9a2d-....",
  "name": "document.pdf",
  "version": 3,
  "created_at": "2025-10-19T05:00:00Z"
}
```

2) Baixar a versão mais recente
- GET /files/{name}/latest
ou
- GET /files/{id}

Exemplo usando name:
```bash
curl -v -o document_downloaded.pdf "http://localhost:5000/files/document.pdf/latest"
```

Exemplo usando id:
```bash
curl -v -o document_downloaded.pdf "http://localhost:5000/files/3f1c9a2d-...."
```

3) Baixar versão específica
- GET /files/{name}/versions/{version}

Exemplo:
```bash
curl -v -o document_v1.pdf "http://localhost:5000/files/document.pdf/versions/1"
```

4) Listar versões de um arquivo
- GET /files/{name}/versions

Exemplo:
```bash
curl -v "http://localhost:5000/files/document.pdf/versions"
```

Resposta (exemplo):
```json
[
  { "version": 1, "id": "aaa", "created_at": "2025-01-01T..." },
  { "version": 2, "id": "bbb", "created_at": "2025-05-01T..." },
  { "version": 3, "id": "ccc", "created_at": "2025-10-19T..." }
]
```

5) Deletar (dependendo da implementação: hard delete ou soft delete)
- DELETE /files/{id}

Exemplo:
```bash
curl -v -X DELETE "http://localhost:5000/files/3f1c9a2d-...."
```

Observações:
- Ajuste os endpoints conforme a implementação do projeto.
- Adapte headers/authorization se a API exigir autenticação.

Como rodar como serviço systemd (exemplo)
Copie o binário para /opt/fsdbslim/ e crie um unit:

```ini
# /etc/systemd/system/fsdbslim.service
[Unit]
Description=FSDBSlim Service
After=network.target

[Service]
Type=simple
WorkingDirectory=/opt/fsdbslim
ExecStart=/opt/fsdbslim/FSDBSlim
Restart=on-failure
Environment=ASPNETCORE_URLS=http://*:5000
Environment=DATABASE_URL=Host=127.0.0.1;Port=5432;Database=fsdb;Username=fsdb;Password=secret

[Install]
WantedBy=multi-user.target
```

Em seguida:
```bash
sudo systemctl daemon-reload
sudo systemctl enable --now fsdbslim
sudo journalctl -u fsdbslim -f
```

Boas práticas e segurança
- Proteja o acesso ao banco: não exponha o PostgreSQL à internet sem controle.
- Use HTTPS na frente do serviço em produção (proxy reverso como nginx, Caddy, Traefik).
- Implemente autenticação/autorização adequada (API keys, JWT, OAuth) antes de expor a API.
- Valide o tamanho e tipo de arquivos (MAX_FILE_SIZE, extensões permitidas).
- Backup regular do PostgreSQL e políticas de retenção/limpeza para arquivos antigos.
- Monitore uso de disco; arquivos em bytea crescem o tamanho do DB e exigem planejamento.

Contribuindo
- Reporte bugs e features pelo sistema de Issues.
- Siga o padrão de codificação do projeto e crie PRs pequenos e testáveis.
- Inclua testes quando possível (unit/integration).
- Documente migrations ou alterações de schema no README ou arquivos de migração.

Licença
- (Coloque aqui a licença do projeto, ex.: MIT) — Se quiser, adicione um arquivo LICENSE com a licença desejada.

Contatos / manutenção
- Repo: https://github.com/ahhvahh/FSDBSlim
- Contato do mantenedor: (adicionar informação de contato se desejar)

---

Se você quiser, eu posso:
- Gerar a migration SQL completa com exemplos de índices e limpeza por data.
- Escrever um systemd unit adaptado ao caminho do binário e usuários específicos.
- Adicionar exemplos de autenticação (Bearer token) e middleware sugerido.
- Atualizar o README diretamente no repositório com estas mudanças (preciso do seu OK para criar o commit).
