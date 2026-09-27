// Seeds the `merchants` collection for the payment gateway (ADR-0010). Mongo runs every *.js in
// /docker-entrypoint-initdb.d once, on first container startup, against MONGO_INITDB_DATABASE
// (payment_gateway). There is no merchant-registration endpoint by design — merchants are seeded.
//
// Demo credentials for POST /api/auth/token (documented fully in the README at stage 9):
//     clientId:     "demo-merchant"
//     clientSecret: "demo-secret"
// `hashedSecret` is the BCrypt hash of "demo-secret" (cost 11). Only the hash is stored — never the
// plaintext (the plaintext appears here only because it is a throwaway demo secret).

// Idempotent upsert ($setOnInsert only writes on first insert): this init script runs only on a
// fresh data dir today, but staying idempotent means it won't collide with the unique index if a
// persistent /data/db volume is added later and the script is ever re-run over existing data.
db.merchants.updateOne(
    { clientId: "demo-merchant" },
    {
        $setOnInsert: {
            merchantId: "11111111-1111-1111-1111-111111111111",
            clientId: "demo-merchant",
            hashedSecret: "$2a$11$dNKDQCN531RpbDqHixGXZe5ppJvgck5e3uJMypafOleXTMHX0yc36",
            createdAt: new Date()
        }
    },
    { upsert: true }
);

// clientId is the lookup key for the token endpoint; enforce uniqueness.
db.merchants.createIndex({ clientId: 1 }, { unique: true });
