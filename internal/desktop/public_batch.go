package desktop

import (
	"context"
	"runtime"
	"slices"

	onepassword "github.com/1password/onepassword-sdk-go"
	"golang.org/x/crypto/ssh"

	"opssh/internal/config"
	"opssh/internal/engine"
)

const publicBatchSize = 32

type batchResolver interface {
	ResolveAll(context.Context, []string) (onepassword.ResolveAllResponse, error)
}

func (c desktopClient) ResolveAll(ctx context.Context, refs []string) (onepassword.ResolveAllResponse, error) {
	result, err := c.client.Secrets().ResolveAll(ctx, refs)
	runtime.KeepAlive(c.client)
	return result, err
}

// PublicBatch returns a canonical public blob or nil for each input in order.
// Earlier successful batches remain available after a later provider error.
func (b *Backend) PublicBatch(ctx context.Context, keys []config.Key) ([][]byte, error) {
	if len(keys) > config.MaxKeys {
		return nil, ErrPublic
	}
	for _, key := range keys {
		if !config.ValidID(key.VaultID) || !config.ValidID(key.ItemID) {
			return nil, ErrPublic
		}
	}
	result := make([][]byte, len(keys))
	start := 0
	for batch := range slices.Chunk(keys, publicBatchSize) {
		blobs, err := execute(ctx, b, func(client resolver) ([][]byte, error) {
			bulk, ok := client.(batchResolver)
			if !ok {
				return nil, ErrUnavailable
			}
			refs := make([]string, len(batch))
			for i, key := range batch {
				refs[i] = "op://" + key.ID() + "/public key"
			}
			response, err := bulk.ResolveAll(ctx, refs)
			if err != nil {
				return nil, SDKError(err)
			}
			if err := ctx.Err(); err != nil {
				return nil, err
			}
			blobs := make([][]byte, len(batch))
			if len(response.IndividualResponses) > len(batch) {
				return blobs, nil
			}
			for i, ref := range refs {
				value := response.IndividualResponses[ref]
				key := batch[i]
				if value.Error == nil && value.Content != nil &&
					value.Content.VaultID == key.VaultID && value.Content.ItemID == key.ItemID {
					blobs[i], _ = parsePublic(value.Content.Secret)
				}
			}
			return blobs, nil
		}, nil)
		if err != nil {
			return result, err
		}
		copy(result[start:], blobs)
		start += len(batch)
	}
	return result, nil
}

func parsePublic(value string) ([]byte, error) {
	if len(value) > 8192 {
		return nil, ErrPublic
	}
	buf := []byte(value)
	defer clear(buf)
	key, _, options, rest, err := ssh.ParseAuthorizedKey(buf)
	if err != nil || len(options) > 0 || len(rest) > 0 {
		return nil, ErrPublic
	}
	blob := key.Marshal()
	if !engine.ValidPublic(blob) {
		return nil, ErrPublic
	}
	return blob, nil
}
